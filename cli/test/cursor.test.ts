import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { main } from '../src/index.js';
import { planInstall, detectAgent } from '../src/install.js';
import { toZip } from '../src/pack.js';
import { readLock } from '../src/lock.js';
import { tmp, enc } from './helpers.js';
import type { ArtifactType } from '../src/types.js';

const ctx = (cwd = '/p', home = '/h') => ({ cwd, home });
const rel = (plan: ReturnType<typeof planInstall>, root = '/p') => plan.writes.map((w) => path.relative(root, w.path)).sort();
const text = (plan: ReturnType<typeof planInstall>, i = 0) => new TextDecoder().decode(plan.writes[i].data);

test('cursor : un skill devient une règle .mdc avec sa description, sans le frontmatter d\'origine', () => {
  const plan = planInstall('Skill', 'pdf', 'cursor', false, { 'SKILL.md': enc('---\nname: pdf\ndescription: "Lit des PDF"\n---\n# PDF\nCorps.\n') }, ctx());
  assert.deepEqual(rel(plan), ['.cursor/rules/pdf.mdc']);
  assert.equal(text(plan), '---\ndescription: "Lit des PDF"\nalwaysApply: false\n---\n\n# PDF\nCorps.\n');
  assert.deepEqual(plan.warnings, []);
});

test('cursor : les fichiers complémentaires d\'un skill sont signalés, pas ignorés en silence', () => {
  const plan = planInstall('Skill', 'pdf', 'cursor', false, { 'SKILL.md': enc('---\ndescription: d\n---\n# x'), 'scripts/a.sh': enc('echo'), 'README.md': enc('r') }, ctx());
  assert.deepEqual(rel(plan), ['.cursor/rules/pdf.mdc']);
  assert.match(plan.warnings[0], /2 fichier\(s\) complémentaire\(s\)/);
});

test('cursor : prompt → commande, MCP → .cursor/mcp.json (local et global), agents et hooks refusés', () => {
  const prompt = planInstall('Prompt', 'resume', 'cursor', false, { 'resume.prompt.md': enc('---\ndescription: d\n---\nRésume ce texte.') }, ctx());
  assert.deepEqual(rel(prompt), ['.cursor/commands/resume.md']);
  assert.equal(text(prompt), 'Résume ce texte.\n');

  const mcp = { 'mcp.json': enc('{"mcpServers":{"x":{"command":"npx","args":["-y","srv"]}}}') };
  assert.equal(planInstall('McpServer', 'git', 'cursor', false, mcp, ctx()).merges[0].path, path.join('/p', '.cursor/mcp.json'));
  assert.equal(planInstall('McpServer', 'git', 'cursor', true, mcp, ctx()).merges[0].path, path.join('/h', '.cursor/mcp.json'));

  for (const t of ['Agent', 'Hook'] as ArtifactType[]) assert.throws(() => planInstall(t, 'x', 'cursor', false, { 'a.agent.md': enc('x'), 'hooks.json': enc('{}') }, ctx()), /non supporté pour cursor/);
  assert.throws(() => planInstall('Skill', 'x', 'cursor', true, { 'SKILL.md': enc('x') }, ctx()), /en global/);
});

test('cursor : le dossier .cursor désigne l\'agent cible', () => {
  const cwd = tmp(); fs.mkdirSync(path.join(cwd, '.cursor'));
  assert.equal(detectAgent(cwd), 'cursor');
});

test('add --agent cursor écrit la règle, le lockfile et affiche l\'avertissement', async () => {
  const zip = toZip({ 'SKILL.md': enc('---\nname: pdf\ndescription: Lit des PDF\n---\n# PDF'), 'README.md': enc('r') });
  const f = (async (url: string) => {
    const u = new URL(url);
    const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status, headers: { 'content-type': 'application/json' } });
    if (u.pathname === '/api/catalog/by-name/myregistry/pdf') return json({ id: 'id1', namespace: 'myregistry', name: 'pdf', type: 'Skill', version: '1.0.0', contentHash: 'h1' });
    if (u.pathname === '/api/catalog/id1/download') return new Response(zip, { headers: { 'x-artifact-sha256': 'h1' } });
    return json({}, 404);
  }) as unknown as typeof fetch;
  const cwd = tmp(); const out: string[] = []; const err: string[] = [];
  const code = await main(['add', 'myregistry/pdf', '--agent', 'cursor'], { cwd, home: tmp(), env: { MYREGISTRY_BASE_URL: 'http://reg' }, out: (s) => out.push(s), err: (s) => err.push(s), fetch: f });
  assert.equal(code, 0, err.join('\n'));
  assert.ok(fs.readFileSync(path.join(cwd, '.cursor/rules/pdf.mdc'), 'utf8').includes('description: "Lit des PDF"'));
  assert.match(err.join('\n'), /AVERT\. 1 fichier\(s\) complémentaire\(s\)/);
  assert.deepEqual(readLock(path.join(cwd, 'myregistry.lock')).artifacts['myregistry/pdf'].files, ['.cursor/rules/pdf.mdc']);
});
