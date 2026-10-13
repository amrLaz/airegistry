import fs from 'node:fs';
import path from 'node:path';
import { CliError } from './types.js';
import { canonicalize } from './names.js';

export interface Edit {
  agent: 'vscode' | 'claude';
  file: string;
  /** Applique la modification sur l'objet JSON déjà lu et le retourne. */
  apply: (json: Record<string, any>) => Record<string, any>;
  description: string;
}

export interface RegistryPlan {
  edits: Edit[];
  commands: string[];
  notes: string[];
}

interface PlanOptions { url: string; title?: string; global: boolean; cwd: string; home: string; platform?: NodeJS.Platform; env?: NodeJS.ProcessEnv }

export function vscodeUserSettings(home: string, platform: NodeJS.Platform, env: NodeJS.ProcessEnv = {}): string {
  if (platform === 'darwin') return path.join(home, 'Library', 'Application Support', 'Code', 'User', 'settings.json');
  if (platform === 'win32') return path.join(env.APPDATA ?? path.join(home, 'AppData', 'Roaming'), 'Code', 'User', 'settings.json');
  return path.join(home, '.config', 'Code', 'User', 'settings.json');
}

export function marketplaceName(title: string | undefined): string {
  return canonicalize(title ?? '') || 'myregistry';
}

/** Calcule la configuration du marketplace (dépôt Git du snapshot) pour VS Code et Claude Code ; Codex : instructions seulement. */
export function planAddRegistry(o: PlanOptions): RegistryPlan {
  if (!/^(file:\/\/\/|https:\/\/|ssh:\/\/|git@)/.test(o.url)) throw new CliError(`URL de dépôt non supportée : ${o.url} (attendu : file:///…, https://… ou ssh).`);
  const name = marketplaceName(o.title);
  const vscodeFile = o.global ? vscodeUserSettings(o.home, o.platform ?? process.platform, o.env) : path.join(o.cwd, '.vscode', 'settings.json');
  const claudeFile = o.global ? path.join(o.home, '.claude', 'settings.json') : path.join(o.cwd, '.claude', 'settings.json');

  return {
    edits: [
      {
        agent: 'vscode', file: vscodeFile,
        description: `VS Code : ajoute le dépôt à « chat.plugins.marketplaces »`,
        apply: (j) => {
          const list: string[] = Array.isArray(j['chat.plugins.marketplaces']) ? j['chat.plugins.marketplaces'] : [];
          return { ...j, 'chat.plugins.marketplaces': list.includes(o.url) ? list : [...list, o.url] };
        },
      },
      {
        agent: 'claude', file: claudeFile,
        description: `Claude Code : déclare le marketplace « ${name} » dans « extraKnownMarketplaces »`,
        apply: (j) => ({ ...j, extraKnownMarketplaces: { ...(j.extraKnownMarketplaces ?? {}), [name]: { source: { source: 'git', url: o.url } } } }),
      },
    ],
    commands: [`claude plugin marketplace add ${o.url}`],
    notes: [
      'Codex : ajoutez le dépôt comme source de plugins avec la commande native de votre version (le manifeste est publié dans .agents/plugins/marketplace.json).',
      'Les noms exacts des réglages dépendent de la version de vos outils : vérifiez après application.',
    ],
  };
}

export type ApplyOutcome = { file: string; status: 'written' | 'unchanged' | 'skipped'; reason?: string };

/** Fusionne chaque modification dans son fichier JSON. Refuse d'écraser un fichier qui n'est pas du JSON strict (ex. settings.json avec commentaires). */
export function applyEdits(edits: Edit[]): ApplyOutcome[] {
  return edits.map((e) => {
    let json: Record<string, any> = {};
    if (fs.existsSync(e.file)) {
      const raw = fs.readFileSync(e.file, 'utf8');
      if (raw.trim().length > 0) {
        try { json = JSON.parse(raw); } catch {
          return { file: e.file, status: 'skipped', reason: 'fichier non JSON strict (commentaires ?) : modification manuelle requise' } as ApplyOutcome;
        }
      }
    }
    const next = e.apply(json);
    if (JSON.stringify(next) === JSON.stringify(json)) return { file: e.file, status: 'unchanged' } as ApplyOutcome;
    fs.mkdirSync(path.dirname(e.file), { recursive: true });
    fs.writeFileSync(e.file, JSON.stringify(next, null, 2) + '\n');
    return { file: e.file, status: 'written' } as ApplyOutcome;
  });
}
