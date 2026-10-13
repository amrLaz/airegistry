import fs from 'node:fs';
import path from 'node:path';
import { CliError, type Agent, type ArtifactType, AGENTS } from './types.js';

export type Files = Record<string, Uint8Array>;

export interface Plan {
  writes: { path: string; data: Uint8Array }[];
  merges: { path: string; topKey: string; entry: string; value: unknown }[];
}

interface Ctx { cwd: string; home: string }

const enc = new TextEncoder();
const dec = new TextDecoder();

/** Détecte l'agent cible depuis les dossiers présents. Échoue si absent ou ambigu (non interactif). */
export function detectAgent(cwd: string): Agent {
  const found = AGENTS.filter((a) => fs.existsSync(path.join(cwd, a === 'copilot' ? '.github' : `.${a}`)));
  if (found.length === 1) return found[0];
  if (found.length === 0) throw new CliError('Agent cible introuvable : précisez --agent copilot|claude|codex.');
  throw new CliError(`Agent cible ambigu (${found.join(', ')}) : précisez --agent.`);
}

function frontmatter(text: string): { fm: Record<string, string>; body: string } {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const fm: Record<string, string> = {};
  if (lines[0]?.trim() !== '---') return { fm, body: text };
  const end = lines.findIndex((l, i) => i > 0 && l.trim() === '---');
  if (end < 0) return { fm, body: text };
  for (const l of lines.slice(1, end)) {
    const m = /^([A-Za-z0-9_-]+)\s*:\s*(.*)$/.exec(l);
    if (m) fm[m[1]] = m[2].trim().replace(/^["']|["']$/g, '');
  }
  return { fm, body: lines.slice(end + 1).join('\n').replace(/^\n+/, '') };
}

/** Convertit un .agent.md en définition Codex (.toml). */
export function agentToToml(name: string, md: string): string {
  const { fm, body } = frontmatter(md);
  const q = (s: string) => JSON.stringify(s);
  return `name = ${q(fm.name ?? name)}\ndescription = ${q(fm.description ?? '')}\ndeveloper_instructions = """\n${body.replace(/"""/g, '\\"\\"\\"').trimEnd()}\n"""\n`;
}

function mcpEntry(files: Files): unknown {
  const raw = files['mcp.json'] ?? files['package.json'];
  if (!raw) throw new CliError('mcp.json introuvable dans l\'archive.');
  let obj = JSON.parse(dec.decode(raw)) as Record<string, any>;
  if (obj.mcpServers && typeof obj.mcpServers === 'object') obj = Object.values(obj.mcpServers)[0] as Record<string, any>;
  if (files['mcp.json'] === undefined) return { command: 'npx', args: ['-y', obj.name] };
  const entry: Record<string, unknown> = {};
  for (const k of ['command', 'args', 'env', 'url', 'transport']) if (obj[k] !== undefined) entry[k] = obj[k];
  return entry;
}

function find(files: Files, pred: (p: string) => boolean): string {
  const p = Object.keys(files).find(pred);
  if (!p) throw new CliError('Fichier principal introuvable dans l\'archive.');
  return p;
}

/** Calcule où et comment installer (docs/04, « Emplacements d'installation »). Pur : n'écrit rien. */
export function planInstall(type: ArtifactType, name: string, agent: Agent, global: boolean, files: Files, ctx: Ctx): Plan {
  const plan: Plan = { writes: [], merges: [] };
  const root = global ? ctx.home : ctx.cwd;
  const put = (rel: string, data: Uint8Array) => plan.writes.push({ path: path.join(root, rel), data });
  const unsupported = (what: string): never => { throw new CliError(`${what} : non supporté pour ${agent}${global ? ' en global' : ''}.`); };

  switch (type) {
    case 'Plugin':
      throw new CliError('Les plugins ne s\'installent pas avec « add » : utilisez les commandes natives de votre agent (voir add-registry).');
    case 'Skill': {
      const base = agent === 'copilot' ? (global ? '.agents/skills' : '.github/skills') : agent === 'claude' ? '.claude/skills' : '.codex/skills';
      for (const [p, data] of Object.entries(files)) put(path.join(base, name, p), data);
      break;
    }
    case 'Agent': {
      const mdPath = find(files, (p) => p.endsWith('.agent.md') || p === 'AGENTS.md');
      if (agent === 'codex') put(path.join('.codex/agents', `${name}.toml`), enc.encode(agentToToml(name, dec.decode(files[mdPath]))));
      else put(path.join(agent === 'copilot' ? (global ? '.agents/agents' : '.github/agents') : '.claude/agents', `${name}.agent.md`), files[mdPath]);
      break;
    }
    case 'Prompt': {
      if (agent !== 'copilot') unsupported('Les prompts');
      if (global) unsupported('Les prompts');
      put(path.join('.github/prompts', `${name}.prompt.md`), files[find(files, (p) => p.endsWith('.prompt.md'))]);
      break;
    }
    case 'Hook': {
      if (agent === 'codex') unsupported('Les hooks');
      if (global) unsupported('Les hooks');
      if (agent === 'copilot') put('.github/hooks/hooks.json', files['hooks.json']);
      else for (const [p, data] of Object.entries(files)) put(path.join('.claude/hooks', name, p), data);
      break;
    }
    case 'McpServer': {
      const entry = mcpEntry(files);
      if (agent === 'copilot') {
        if (global) unsupported('Les serveurs MCP');
        plan.merges.push({ path: path.join(ctx.cwd, '.vscode/mcp.json'), topKey: 'servers', entry: name, value: entry });
      } else if (agent === 'claude') {
        plan.merges.push({ path: global ? path.join(ctx.home, '.claude.json') : path.join(ctx.cwd, '.mcp.json'), topKey: 'mcpServers', entry: name, value: entry });
      } else {
        plan.merges.push({ path: path.join(root, '.codex/mcp.json'), topKey: 'mcpServers', entry: name, value: entry });
      }
      break;
    }
  }
  return plan;
}

export function applyPlan(plan: Plan): { files: string[]; merged: { path: string; topKey: string; entry: string }[] } {
  for (const w of plan.writes) {
    fs.mkdirSync(path.dirname(w.path), { recursive: true });
    fs.writeFileSync(w.path, w.data);
  }
  for (const m of plan.merges) {
    let json: Record<string, any> = {};
    try { json = JSON.parse(fs.readFileSync(m.path, 'utf8')); } catch { /* nouveau fichier */ }
    json[m.topKey] = { ...(json[m.topKey] ?? {}), [m.entry]: m.value };
    fs.mkdirSync(path.dirname(m.path), { recursive: true });
    fs.writeFileSync(m.path, JSON.stringify(json, null, 2) + '\n');
  }
  return { files: plan.writes.map((w) => w.path), merged: plan.merges.map(({ path: p, topKey, entry }) => ({ path: p, topKey, entry })) };
}

export function removeInstalled(files: string[], merged: { path: string; topKey: string; entry: string }[] = []): void {
  for (const f of files) {
    fs.rmSync(f, { force: true });
    // supprime les dossiers parents vides (jusqu'à la racine du projet)
    for (let d = path.dirname(f); fs.existsSync(d) && fs.readdirSync(d).length === 0; d = path.dirname(d)) {
      if (['.github', '.claude', '.codex', '.agents'].includes(path.basename(d))) { fs.rmdirSync(d); break; }
      fs.rmdirSync(d);
    }
  }
  for (const m of merged) {
    try {
      const json = JSON.parse(fs.readFileSync(m.path, 'utf8'));
      delete json[m.topKey]?.[m.entry];
      fs.writeFileSync(m.path, JSON.stringify(json, null, 2) + '\n');
    } catch { /* fichier déjà absent */ }
  }
}
