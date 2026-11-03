import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { main } from '../src/index.js';
import { browserLogin, type Io } from '../src/auth.js';
import { tmp } from './helpers.js';

const TOKEN = 'mrt_testtoken_abcdefghijklmnopqrstuvwxyz0123456789';

/** Simule le navigateur : appelle le callback local avec le jeton et le state reçus. */
const browserThatApproves = (opts: { state?: (s: string) => string } = {}): Io => ({
  timeoutMs: 2000,
  edit: async () => {},
  openUrl: async (url) => {
    const u = new URL(url);
    assert.equal(u.pathname, '/cli/auth');
    const cb = new URL(u.searchParams.get('callback')!);
    assert.equal(cb.hostname, 'localhost');
    const state = u.searchParams.get('state')!;
    cb.searchParams.set('token', TOKEN);
    cb.searchParams.set('state', opts.state ? opts.state(state) : state);
    setTimeout(() => { void fetch(cb).catch(() => {}); }, 10);
  },
});

test('browserLogin renvoie le jeton quand le state correspond', async () => {
  assert.equal(await browserLogin('http://reg', browserThatApproves(), () => {}), TOKEN);
});

test('browserLogin ignore un callback au mauvais state puis expire', async () => {
  const io = { ...browserThatApproves({ state: () => 'forged-state-value' }), timeoutMs: 300 };
  await assert.rejects(() => browserLogin('http://reg', io, () => {}), /expirée/);
});

interface Server { authed: boolean; calls: { path: string; auth?: string; body?: any }[]; fetch: typeof fetch }
function server(opts: { me?: 'none' | 'anonymous' | 'user'; source?: any; sourceStatus?: number; importOk?: boolean } = {}): Server {
  const s: Server = { authed: false, calls: [], fetch: undefined as any };
  s.fetch = (async (url: string, init?: RequestInit) => {
    const u = new URL(url);
    const auth = (init?.headers as Record<string, string> | undefined)?.Authorization;
    s.calls.push({ path: u.pathname + u.search, auth, body: init?.body ? JSON.parse(init.body as string) : undefined });
    const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status, headers: { 'content-type': 'application/json' } });
    if (u.pathname === '/api/me') {
      if (opts.me === 'none') return json({}, 404);
      const ok = opts.me === 'user' || auth === `Bearer ${TOKEN}`;
      return json({ email: ok ? 'amr@example.com' : '', role: 'Publisher', authenticated: ok });
    }
    if (u.pathname === '/api/registry-info') return json({ defaultNamespace: 'myregistry' });
    if (u.pathname === '/api/cli/publish/suggested-version') return json({ version: '1.0.1' });
    if (u.pathname === '/api/cli/edit/source') return opts.sourceStatus ? json({}, opts.sourceStatus) : json(opts.source);
    if (u.pathname === '/api/import') return opts.importOk === false ? json({ title: 'Publication refusée', errors: [{ code: 'x', message: 'invalide' }] }, 422) : json({ success: true, version: '1.0.1', grade: 'A', warnings: [] }, 201);
    return json({}, 404);
  }) as unknown as typeof fetch;
  return s;
}

async function run(args: string[], srv: Server, io: Io, extra: { env?: NodeJS.ProcessEnv; home?: string; cwd?: string } = {}) {
  const out: string[] = []; const err: string[] = [];
  const code = await main(args, { cwd: extra.cwd ?? tmp(), home: extra.home ?? tmp(), env: { MYREGISTRY_BASE_URL: 'http://reg', ...extra.env }, out: (s) => out.push(s), err: (s) => err.push(s), fetch: srv.fetch, io });
  return { code, out: out.join('\n'), err: err.join('\n') };
}

const skillDir = () => {
  const d = path.join(tmp(), 'resume-reunion');
  fs.mkdirSync(d); fs.writeFileSync(path.join(d, 'SKILL.md'), '---\nname: resume-reunion\ndescription: d\n---\n# R');
  return d;
};

test('login affiche l\'utilisateur et n\'écrit jamais le jeton sur disque', async () => {
  const home = tmp(); const cwd = tmp();
  const r = await run(['login'], server(), browserThatApproves(), { home, cwd });
  assert.equal(r.code, 0);
  assert.match(r.out, /amr@example\.com \(Publisher\)/);
  const written: string[] = [];
  for (const root of [home, cwd]) for (const f of fs.readdirSync(root, { recursive: true }) as string[]) {
    const p = path.join(root, f); if (fs.statSync(p).isFile()) written.push(fs.readFileSync(p, 'utf8'));
  }
  assert.ok(written.every((t) => !t.includes(TOKEN)));
});

test('publish lance la connexion navigateur si le serveur ne reconnaît pas l\'utilisateur, puis envoie le jeton', async () => {
  const srv = server({ me: 'anonymous' });
  const r = await run(['publish', skillDir()], srv, browserThatApproves());
  assert.equal(r.code, 0, r.err);
  assert.equal(srv.calls.find((c) => c.path === '/api/import')?.auth, `Bearer ${TOKEN}`);
});

test('publish ne rouvre pas le navigateur si l\'utilisateur est déjà reconnu ou si /api/me est absent', async () => {
  const never: Io = { timeoutMs: 100, edit: async () => {}, openUrl: () => { throw new Error('ne devrait pas ouvrir le navigateur'); } };
  for (const me of ['user', 'none'] as const) {
    const r = await run(['publish', skillDir()], server({ me }), never);
    assert.equal(r.code, 0, r.err);
  }
});

test('MYREGISTRY_API_TOKEN évite le navigateur et part dans l\'en-tête', async () => {
  const srv = server({ me: 'anonymous' });
  const never: Io = { timeoutMs: 100, edit: async () => {}, openUrl: () => { throw new Error('ne devrait pas ouvrir le navigateur'); } };
  const r = await run(['publish', skillDir()], srv, never, { env: { MYREGISTRY_API_TOKEN: 'mrt_ci_token' } });
  assert.equal(r.code, 0, r.err);
  assert.equal(srv.calls.find((c) => c.path === '/api/import')?.auth, 'Bearer mrt_ci_token');
});

const b64 = (s: string) => Buffer.from(s).toString('base64');
const source = (name: string, files: Record<string, string>) => ({
  id: 'v1', namespace: 'myregistry', name, type: 'Skill', version: '1.0.0', description: 'Résume', status: 'Pending', tags: ['réunion'],
  files: Object.entries(files).map(([p, c]) => ({ path: p, isBinary: false, content: b64(c) })),
});
const uniq = () => 'ed' + Math.random().toString(36).slice(2, 8).replace(/\d/g, 'x');

test('edit télécharge les sources, laisse éditer puis publie une nouvelle version', async () => {
  const name = uniq();
  const srv = server({ me: 'user', source: source(name, { 'SKILL.md': `---\nname: ${name}\ndescription: d\n---\n# v1`, 'README.md': '# r' }) });
  const io: Io = { timeoutMs: 100, openUrl: () => {}, edit: async (dir) => { fs.appendFileSync(path.join(dir, 'SKILL.md'), '\nmodifié'); } };
  const r = await run(['edit', `myregistry/${name}`], srv, io);
  assert.equal(r.code, 0, r.err);
  const imp = srv.calls.find((c) => c.path === '/api/import')!.body;
  assert.equal(imp.name, name); assert.equal(imp.namespace, 'myregistry'); assert.equal(imp.type, 'Skill'); assert.equal(imp.version, '1.0.1');
  assert.match(imp.rawContent, /modifié/);
  assert.equal(imp.additionalFiles['README.md'], '# r');
  assert.deepEqual(imp.tags, ['réunion']);
  assert.equal(fs.existsSync(path.join(os.tmpdir(), 'myregistry-edit', `myregistry-${name}`)), false, 'le dossier de travail est nettoyé après succès');
});

test('edit conserve le dossier après un échec et --resume ne retélécharge pas', async () => {
  const name = uniq();
  const first = server({ me: 'user', source: source(name, { 'SKILL.md': `---\nname: ${name}\ndescription: d\n---\n# v1` }), importOk: false });
  const io: Io = { timeoutMs: 100, openUrl: () => {}, edit: async (dir) => { fs.appendFileSync(path.join(dir, 'SKILL.md'), '\nencours'); } };
  assert.equal((await run(['edit', `myregistry/${name}`], first, io)).code, 1);

  const second = server({ me: 'user', importOk: true });
  const r = await run(['edit', `myregistry/${name}`, '--resume'], second, { ...io, edit: async () => {} });
  assert.equal(r.code, 0, r.err);
  assert.equal(second.calls.some((c) => c.path.startsWith('/api/cli/edit/source')), false);
  assert.match(second.calls.find((c) => c.path === '/api/import')!.body.rawContent, /encours/);
});

test('edit refuse un chemin qui sort du dossier de travail', async () => {
  const name = uniq();
  const srv = server({ me: 'user', source: source(name, { '../evil.txt': 'x', 'SKILL.md': '# a' }) });
  const r = await run(['edit', `myregistry/${name}`], srv, { timeoutMs: 100, openUrl: () => {}, edit: async () => {} });
  assert.notEqual(r.code, 0);
  assert.match(r.err, /Chemin refusé/);
  assert.equal(srv.calls.some((c) => c.path === '/api/import'), false);
});

test('edit : droits insuffisants, introuvable et référence invalide', async () => {
  const io: Io = { timeoutMs: 100, openUrl: () => {}, edit: async () => {} };
  assert.match((await run(['edit', 'myregistry/x'], server({ me: 'user', sourceStatus: 403 }), io)).err, /seuls l'auteur/);
  assert.match((await run(['edit', 'myregistry/x'], server({ me: 'user', sourceStatus: 404 }), io)).err, /introuvable/);
  assert.match((await run(['edit', 'pas-un-slug'], server({ me: 'user' }), io)).err, /Référence invalide/);
  assert.match((await run(['edit'], server({ me: 'user' }), io)).err, /Usage/);
});
