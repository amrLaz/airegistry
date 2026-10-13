import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { main } from '../src/index.js';
import { toZip } from '../src/pack.js';
import { readLock } from '../src/lock.js';
import { tmp, enc } from './helpers.js';

const skillZip = toZip({ 'SKILL.md': enc('---\nname: pdf\ndescription: d\n---\n# PDF'), 'README.md': enc('r') });

/** Faux registre : ne dépend d'aucun serveur. */
function fakeRegistry(opts: { hash?: string; version?: string; sha?: string | null } = {}) {
  const calls: { url: string; method: string; body?: any }[] = [];
  const hash = opts.hash ?? 'h1';
  const f = (async (url: string, init?: RequestInit) => {
    const u = new URL(url);
    calls.push({ url: u.pathname + u.search, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(init.body as string) : undefined });
    const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status, headers: { 'content-type': 'application/json' } });
    if (u.pathname === '/api/catalog/by-name/myregistry/pdf') return json({ id: 'id1', namespace: 'myregistry', name: 'pdf', type: 'Skill', version: opts.version ?? '1.0.0', contentHash: hash });
    if (u.pathname.startsWith('/api/catalog/by-name/')) return json({}, 404);
    if (u.pathname === '/api/catalog/id1/download') return new Response(skillZip, { headers: { 'x-artifact-sha256': opts.sha === undefined ? hash : (opts.sha as string) } });
    if (u.pathname === '/api/catalog') return json({ items: [{ id: 'id1', namespace: 'myregistry', name: 'pdf', type: 'Skill', version: '1.0.0', votes: 3, description: 'PDF' }], total: 1, page: 1, pageSize: 20 });
    if (u.pathname === '/api/registry-info') return json({ defaultNamespace: 'myregistry' });
    if (u.pathname === '/api/cli/publish/suggested-version') return json({ version: '1.0.0' });
    if (u.pathname === '/api/validate') return json({ isValid: true, errors: [], warnings: [{ code: 'missing-readme', message: 'README absent' }], grade: 'B' });
    if (u.pathname === '/api/import') return json({ success: true, version: '1.0.0', grade: 'B', warnings: [] }, 201);
    return json({}, 404);
  }) as unknown as typeof fetch;
  return { fetch: f, calls };
}

async function run(args: string[], cwd: string, home: string, reg = fakeRegistry()) {
  const out: string[] = []; const err: string[] = [];
  const code = await main(args, { cwd, home, env: { MYREGISTRY_BASE_URL: 'http://reg' }, out: (s) => out.push(s), err: (s) => err.push(s), fetch: reg.fetch });
  return { code, out: out.join('\n'), err: err.join('\n'), calls: reg.calls };
}

test('list -r affiche le catalogue distant', async () => {
  const r = await run(['list', '-r'], tmp(), tmp());
  assert.equal(r.code, 0);
  assert.match(r.out, /myregistry\/pdf/);
});

test('add installe, écrit le lockfile, puis list et remove', async () => {
  const cwd = tmp(); const home = tmp();
  const a = await run(['add', 'myregistry/pdf', '--agent', 'claude'], cwd, home);
  assert.equal(a.code, 0, a.err);
  assert.ok(fs.existsSync(path.join(cwd, '.claude/skills/pdf/SKILL.md')));
  const entry = readLock(path.join(cwd, 'myregistry.lock')).artifacts['myregistry/pdf'];
  assert.equal(entry.version, '1.0.0');
  assert.ok(entry.files.every((f) => !path.isAbsolute(f)), 'le lockfile doit être portable (chemins relatifs)');
  assert.ok(entry.files.includes('.claude/skills/pdf/SKILL.md'));
  assert.match((await run(['list'], cwd, home)).out, /myregistry\/pdf/);
  const rm = await run(['remove', 'pdf'], cwd, home);
  assert.equal(rm.code, 0);
  assert.ok(!fs.existsSync(path.join(cwd, '.claude/skills/pdf')));
  assert.deepEqual(readLock(path.join(cwd, 'myregistry.lock')).artifacts, {});
});

test('add échoue en non interactif si l\'agent est ambigu', async () => {
  const cwd = tmp();
  fs.mkdirSync(path.join(cwd, '.claude')); fs.mkdirSync(path.join(cwd, '.github'));
  const r = await run(['add', 'myregistry/pdf'], cwd, tmp());
  assert.equal(r.code, 1);
  assert.match(r.err, /ambigu/);
});

test('add refuse une empreinte SHA-256 inattendue et n\'écrit rien', async () => {
  const cwd = tmp();
  const r = await run(['add', 'myregistry/pdf', '--agent', 'claude'], cwd, tmp(), fakeRegistry({ sha: 'autre' }));
  assert.equal(r.code, 1);
  assert.match(r.err, /SHA-256/);
  assert.ok(!fs.existsSync(path.join(cwd, '.claude')));
});

test('add : artefact introuvable', async () => {
  const r = await run(['add', 'myregistry/nope', '--agent', 'claude'], tmp(), tmp());
  assert.equal(r.code, 1);
  assert.match(r.err, /Introuvable/);
});

test('outdated : code 1 si le hash distant a changé, update remet à jour', async () => {
  const cwd = tmp(); const home = tmp();
  await run(['add', 'myregistry/pdf', '--agent', 'claude'], cwd, home);
  assert.equal((await run(['outdated'], cwd, home)).code, 0);
  const newer = fakeRegistry({ hash: 'h2', version: '1.1.0' });
  const o = await run(['outdated'], cwd, home, newer);
  assert.equal(o.code, 1);
  assert.match(o.out, /1\.0\.0 → 1\.1\.0/);
  const u = await run(['update'], cwd, home, newer);
  assert.equal(u.code, 0, u.err);
  assert.equal(readLock(path.join(cwd, 'myregistry.lock')).artifacts['myregistry/pdf'].version, '1.1.0');
});

test('publish --dry-run valide sans publier', async () => {
  const proj = tmp();
  fs.mkdirSync(path.join(proj, 'pdf-extractor'));
  fs.writeFileSync(path.join(proj, 'pdf-extractor', 'SKILL.md'), '---\nname: pdf-extractor\ndescription: d\n---\n# x');
  const r = await run(['publish', 'pdf-extractor', '--dry-run'], proj, tmp());
  assert.equal(r.code, 0, r.err);
  assert.match(r.out, /Rien n'a été publié/);
  assert.ok(!r.calls.some((c) => c.url === '/api/import'));
});

test('publish envoie le contenu et affiche Pending', async () => {
  const proj = tmp();
  fs.mkdirSync(path.join(proj, 'pdf-extractor'));
  fs.writeFileSync(path.join(proj, 'pdf-extractor', 'SKILL.md'), '---\nname: pdf-extractor\ndescription: d\n---\n# x');
  fs.writeFileSync(path.join(proj, 'pdf-extractor', 'README.md'), '# r');
  const r = await run(['publish', 'pdf-extractor', '--tags', 'pdf, ocr'], proj, tmp());
  assert.equal(r.code, 0, r.err);
  assert.match(r.out, /Pending/);
  const body = r.calls.find((c) => c.url === '/api/import')!.body;
  assert.equal(body.type, 'Skill');
  assert.equal(body.name, 'pdf-extractor');
  assert.equal(body.fileName, 'SKILL.md');
  assert.deepEqual(body.tags, ['pdf', 'ocr']);
  assert.equal(body.additionalFiles['README.md'], '# r');
});

test('check : nom non conforme → code 1, forme canonique proposée', async () => {
  const proj = tmp();
  fs.mkdirSync(path.join(proj, 'Bad_Name'));
  fs.writeFileSync(path.join(proj, 'Bad_Name', 'SKILL.md'), '---\nname: x\ndescription: d\n---');
  const r = await run(['check', 'Bad_Name'], proj, tmp());
  assert.equal(r.code, 1);
  assert.match(r.err, /name-format/);
  assert.match(r.err, /bad-name/);
});

test('check : registre injoignable → avertissement mais contrôle local seul', async () => {
  const proj = tmp();
  fs.mkdirSync(path.join(proj, 'pdf-extractor'));
  fs.writeFileSync(path.join(proj, 'pdf-extractor', 'SKILL.md'), '---\nname: x\ndescription: d\n---');
  const reg = { fetch: (async () => { throw new Error('down'); }) as unknown as typeof fetch, calls: [] };
  const r = await run(['check', 'pdf-extractor'], proj, tmp(), reg);
  assert.equal(r.code, 0);
  assert.match(r.err, /contrôle serveur ignoré/);
});

test('pack écrit un zip', async () => {
  const proj = tmp();
  fs.mkdirSync(path.join(proj, 'pdf-extractor'));
  fs.writeFileSync(path.join(proj, 'pdf-extractor', 'SKILL.md'), '# s');
  const r = await run(['pack', 'pdf-extractor'], proj, tmp());
  assert.equal(r.code, 0);
  assert.ok(fs.existsSync(path.join(proj, 'pdf-extractor.zip')));
});

test('config écrit et relit l\'URL du registre', async () => {
  const cwd = tmp();
  await run(['config', '--registry-base-url', 'http://x:1'], cwd, tmp());
  assert.equal(JSON.parse(fs.readFileSync(path.join(cwd, '.myregistry.json'), 'utf8')).registryBaseUrl, 'http://x:1');
});

test('commande inconnue et non implémentée', async () => {
  assert.equal((await run(['bidule'], tmp(), tmp())).code, 1);
  const l = await run(['login'], tmp(), tmp());
  assert.equal(l.code, 1);
  assert.match(l.err, /pas encore implémentée/);
});

test('plugins refusés par outdated --type Plugin', async () => {
  const r = await run(['outdated', '--type', 'Plugin'], tmp(), tmp());
  assert.equal(r.code, 1);
});
