import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { planAddRegistry, applyEdits, vscodeUserSettings, marketplaceName } from '../src/registry.js';
import { main } from '../src/index.js';
import { tmp } from './helpers.js';

const URL_ = 'file:///srv/myregistry/marketplace';

test('plan projet : VS Code et Claude Code, avec commande native', () => {
  const cwd = tmp(); const home = tmp();
  const p = planAddRegistry({ url: URL_, title: 'Acme Registry', global: false, cwd, home });
  assert.deepEqual(p.edits.map((e) => path.relative(cwd, e.file)), [path.join('.vscode', 'settings.json'), path.join('.claude', 'settings.json')]);
  assert.deepEqual(p.commands, [`claude plugin marketplace add ${URL_}`]);
  assert.ok(p.notes.some((n) => n.includes('Codex')));
});

test('plan global : fichiers utilisateur selon la plateforme', () => {
  const home = '/home/u';
  assert.equal(vscodeUserSettings(home, 'linux'), path.join(home, '.config', 'Code', 'User', 'settings.json'));
  assert.equal(vscodeUserSettings(home, 'darwin'), path.join(home, 'Library', 'Application Support', 'Code', 'User', 'settings.json'));
  assert.equal(vscodeUserSettings(home, 'win32', { APPDATA: '/appdata' }), path.join('/appdata', 'Code', 'User', 'settings.json'));
  const p = planAddRegistry({ url: URL_, global: true, cwd: '/x', home, platform: 'linux' });
  assert.equal(p.edits[1].file, path.join(home, '.claude', 'settings.json'));
});

test('nom du marketplace dérivé du titre', () => {
  assert.equal(marketplaceName('Acme Registry'), 'acme-registry');
  assert.equal(marketplaceName(undefined), 'myregistry');
  assert.equal(marketplaceName('!!!'), 'myregistry');
});

test('URL non supportée refusée', () => {
  assert.throws(() => planAddRegistry({ url: 'ftp://x', global: false, cwd: tmp(), home: tmp() }), /non supportée/);
  assert.throws(() => planAddRegistry({ url: '/chemin/local', global: false, cwd: tmp(), home: tmp() }), /non supportée/);
});

test('applyEdits fusionne sans écraser et reste idempotent', () => {
  const cwd = tmp(); const home = tmp();
  fs.mkdirSync(path.join(cwd, '.vscode'));
  fs.writeFileSync(path.join(cwd, '.vscode', 'settings.json'), JSON.stringify({ 'editor.tabSize': 2, 'chat.plugins.marketplaces': ['org/autre'] }));
  const plan = planAddRegistry({ url: URL_, title: 'Acme', global: false, cwd, home });
  const first = applyEdits(plan.edits);
  assert.deepEqual(first.map((r) => r.status), ['written', 'written']);
  const vs = JSON.parse(fs.readFileSync(path.join(cwd, '.vscode', 'settings.json'), 'utf8'));
  assert.equal(vs['editor.tabSize'], 2);
  assert.deepEqual(vs['chat.plugins.marketplaces'], ['org/autre', URL_]);
  const cl = JSON.parse(fs.readFileSync(path.join(cwd, '.claude', 'settings.json'), 'utf8'));
  assert.deepEqual(cl.extraKnownMarketplaces.acme, { source: { source: 'git', url: URL_ } });
  assert.deepEqual(applyEdits(plan.edits).map((r) => r.status), ['unchanged', 'unchanged']);
});

test('un settings.json avec commentaires n\'est jamais écrasé', () => {
  const cwd = tmp();
  fs.mkdirSync(path.join(cwd, '.vscode'));
  const original = '{\n  // mon commentaire\n  "a": 1\n}\n';
  fs.writeFileSync(path.join(cwd, '.vscode', 'settings.json'), original);
  const r = applyEdits(planAddRegistry({ url: URL_, global: false, cwd, home: tmp() }).edits);
  assert.equal(r[0].status, 'skipped');
  assert.equal(fs.readFileSync(path.join(cwd, '.vscode', 'settings.json'), 'utf8'), original);
});

function fakeInfo(marketplaceRepo: string | null) {
  return (async (url: string) => {
    if (new URL(url).pathname === '/api/registry-info') return new Response(JSON.stringify({ title: 'Acme', defaultNamespace: 'acme', marketplaceRepo }), { headers: { 'content-type': 'application/json' } });
    return new Response('{}', { status: 404 });
  }) as unknown as typeof fetch;
}

async function run(args: string[], cwd: string, home: string, f: typeof fetch) {
  const out: string[] = []; const err: string[] = [];
  const code = await main(args, { cwd, home, env: { MYREGISTRY_BASE_URL: 'http://reg' }, out: (s) => out.push(s), err: (s) => err.push(s), fetch: f });
  return { code, out: out.join('\n'), err: err.join('\n') };
}

test('add-registry sans --apply : décrit sans rien écrire', async () => {
  const cwd = tmp();
  const r = await run(['add-registry'], cwd, tmp(), fakeInfo(URL_));
  assert.equal(r.code, 0);
  assert.match(r.out, /claude plugin marketplace add/);
  assert.match(r.out, /Aucune modification/);
  assert.ok(!fs.existsSync(path.join(cwd, '.claude')));
});

test('add-registry --apply écrit les fichiers', async () => {
  const cwd = tmp();
  const r = await run(['add-registry', '--apply'], cwd, tmp(), fakeInfo(URL_));
  assert.equal(r.code, 0, r.err);
  assert.ok(fs.existsSync(path.join(cwd, '.claude', 'settings.json')));
  assert.ok(fs.existsSync(path.join(cwd, '.vscode', 'settings.json')));
});

test('add-registry : échec clair si le registre ne publie pas de marketplace', async () => {
  const r = await run(['add-registry'], tmp(), tmp(), fakeInfo(null));
  assert.equal(r.code, 1);
  assert.match(r.err, /Aucun dépôt de marketplace/);
});

test('add-registry --marketplace prime sur le registre', async () => {
  const r = await run(['add-registry', '--marketplace', 'https://git.example.io/org/market.git'], tmp(), tmp(), fakeInfo(null));
  assert.equal(r.code, 0);
  assert.match(r.out, /git\.example\.io/);
});

test('add-registry --apply : code 1 si un fichier a dû être ignoré', async () => {
  const cwd = tmp();
  fs.mkdirSync(path.join(cwd, '.vscode'));
  fs.writeFileSync(path.join(cwd, '.vscode', 'settings.json'), '{ // c\n}');
  const r = await run(['add-registry', '--apply'], cwd, tmp(), fakeInfo(URL_));
  assert.equal(r.code, 1);
  assert.match(r.err, /IGNORÉ/);
  assert.ok(fs.existsSync(path.join(cwd, '.claude', 'settings.json'))); // l'autre fichier est quand même écrit
});
