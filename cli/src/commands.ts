import fs from 'node:fs';
import path from 'node:path';
import { Api } from './api.js';
import { configPath, resolveConfig, writeConfig, type Config } from './config.js';
import { applyPlan, detectAgent, planInstall, removeInstalled } from './install.js';
import { readLock, writeLock, lockPath, type LockEntry } from './lock.js';
import { checkName, parseSlug } from './names.js';
import { detectType, fromZip, isBinary, mainFileFor, readArtifact, toZip } from './pack.js';
import { AGENTS, ARTIFACT_TYPES, CliError, type Agent, type ArtifactType } from './types.js';

export interface Ctx {
  cwd: string;
  home: string;
  config: Config;
  api: Api;
  out: (s: string) => void;
  err: (s: string) => void;
}

export type Opts = Record<string, string | boolean | string[] | undefined>;
const str = (o: Opts, k: string): string | undefined => (typeof o[k] === 'string' ? (o[k] as string) : undefined);

function asAgent(v: string | undefined): Agent | undefined {
  if (v === undefined) return undefined;
  if (!AGENTS.includes(v as Agent)) throw new CliError(`Agent inconnu : ${v} (copilot | claude | codex).`);
  return v as Agent;
}

function asType(v: string | undefined): ArtifactType | undefined {
  if (v === undefined) return undefined;
  const t = ARTIFACT_TYPES.find((x) => x.toLowerCase() === v.toLowerCase());
  if (!t) throw new CliError(`Type inconnu : ${v} (${ARTIFACT_TYPES.join(' | ')}).`);
  return t;
}

export function cmdConfig(ctx: Ctx, o: Opts): number {
  const url = str(o, 'registry-base-url');
  if (url) {
    const file = configPath(!!o.global, ctx.cwd, ctx.home);
    writeConfig(file, { registryBaseUrl: url });
    ctx.out(`Config écrite : ${file}`);
    return 0;
  }
  ctx.out(JSON.stringify({ baseUrl: ctx.config.baseUrl, apiToken: ctx.config.apiToken ? '***' : undefined }, null, 2));
  return 0;
}

export async function cmdListRemote(ctx: Ctx, o: Opts): Promise<number> {
  const page = await ctx.api.list({
    type: asType(str(o, 'type')), search: str(o, 'search'), tag: str(o, 'tag'),
    page: str(o, 'page') ? Number(str(o, 'page')) : undefined,
    pageSize: str(o, 'page-size') ? Number(str(o, 'page-size')) : undefined,
  });
  if (o.json) { ctx.out(JSON.stringify(page, null, 2)); return 0; }
  for (const i of page.items) ctx.out(`${i.type.padEnd(10)} ${`${i.namespace}/${i.name}`.padEnd(40)} v${i.version.padEnd(10)} ▲${i.votes}  ${i.description ?? ''}`);
  ctx.out(`${page.items.length} sur ${page.total} (page ${page.page}).`);
  return 0;
}

export function cmdListLocal(ctx: Ctx, o: Opts): number {
  const lock = readLock(lockPath(!!o.global, ctx.cwd, ctx.home));
  const entries = Object.entries(lock.artifacts);
  if (entries.length === 0) { ctx.out('Aucun artefact installé.'); return 0; }
  for (const [slug, e] of entries) ctx.out(`${e.type.padEnd(10)} ${slug.padEnd(40)} v${e.version}  (${e.agent}${e.global ? ', global' : ''})`);
  return 0;
}

const rootFor = (ctx: Ctx, global: boolean) => (global ? ctx.home : ctx.cwd);
const toRel = (root: string, p: string) => path.relative(root, p).split(path.sep).join('/');
const toAbs = (root: string, p: string) => path.resolve(root, p);

/** Le lockfile est versionné : il ne contient que des chemins relatifs à la racine (projet ou home). */
function absolute(ctx: Ctx, e: LockEntry): { files: string[]; merged: NonNullable<LockEntry['merged']> } {
  const root = rootFor(ctx, e.global);
  return { files: e.files.map((f) => toAbs(root, f)), merged: (e.merged ?? []).map((m) => ({ ...m, path: toAbs(root, m.path) })) };
}

async function install(ctx: Ctx, slug: string, o: Opts, version?: string): Promise<LockEntry> {
  const ref = parseSlug(slug);
  if (!ref) throw new CliError(`Identifiant invalide : « ${slug} » (attendu : namespace/nom).`);
  const found = await ctx.api.byName(ref.ns, ref.name, version);
  if (!found) throw new CliError(`Introuvable (ou non approuvé) : ${slug}.`);
  if (found.type === 'Plugin') throw new CliError('Les plugins ne s\'installent pas avec « add » : utilisez les commandes natives de votre agent (voir add-registry).');

  const { data, sha256 } = await ctx.api.download(found.id);
  if (sha256 && sha256 !== found.contentHash) throw new CliError('Empreinte SHA-256 inattendue : installation annulée.');
  const files = fromZip(data);
  const global = !!o.g || !!o.global;
  const agent = asAgent(str(o, 'agent')) ?? detectAgent(ctx.cwd);
  const plan = planInstall(found.type, ref.name, agent, global, files, { cwd: ctx.cwd, home: ctx.home });
  const done = applyPlan(plan);
  const root = rootFor(ctx, global);
  return {
    id: found.id, version: found.version, hash: found.contentHash, type: found.type, agent, global,
    files: done.files.map((f) => toRel(root, f)), merged: done.merged.map((m) => ({ ...m, path: toRel(root, m.path) })),
  };
}

export async function cmdAdd(ctx: Ctx, slug: string, o: Opts): Promise<number> {
  const entry = await install(ctx, slug, o, str(o, 'version'));
  const lf = lockPath(entry.global, ctx.cwd, ctx.home);
  const lock = readLock(lf);
  lock.artifacts[slug] = entry;
  writeLock(lf, lock);
  ctx.out(`Installé ${slug}@${entry.version} (${entry.type}) pour ${entry.agent}.`);
  for (const f of entry.files) ctx.out(`  + ${f}`);
  return 0;
}

export function cmdRemove(ctx: Ctx, slug: string, o: Opts): number {
  const lf = lockPath(!!o.g || !!o.global, ctx.cwd, ctx.home);
  const lock = readLock(lf);
  const key = lock.artifacts[slug] ? slug : Object.keys(lock.artifacts).find((k) => k.endsWith(`/${slug}`));
  if (!key) throw new CliError(`Non installé : ${slug}.`);
  const abs = absolute(ctx, lock.artifacts[key]);
  removeInstalled(abs.files, abs.merged);
  delete lock.artifacts[key];
  writeLock(lf, lock);
  ctx.out(`Supprimé ${key}.`);
  return 0;
}

export async function cmdOutdated(ctx: Ctx, o: Opts): Promise<number> {
  const lock = readLock(lockPath(!!o.g || !!o.global, ctx.cwd, ctx.home));
  const type = asType(str(o, 'type'));
  if (type === 'Plugin') throw new CliError('« outdated » n\'est pas supporté pour les plugins.');
  let outdated = 0;
  for (const [slug, e] of Object.entries(lock.artifacts)) {
    if (type && e.type !== type) continue;
    const ref = parseSlug(slug)!;
    const latest = await ctx.api.byName(ref.ns, ref.name);
    if (latest && latest.contentHash !== e.hash) {
      outdated++;
      ctx.out(`${slug}  ${e.version} → ${latest.version}`);
    } else if (o.all) ctx.out(`${slug}  ${e.version} (à jour)`);
  }
  if (outdated === 0) ctx.out('Tout est à jour.');
  return outdated > 0 ? 1 : 0;
}

export async function cmdUpdate(ctx: Ctx, slug: string | undefined, o: Opts): Promise<number> {
  const global = !!o.g || !!o.global;
  const lf = lockPath(global, ctx.cwd, ctx.home);
  const lock = readLock(lf);
  const targets = slug ? [slug] : Object.keys(lock.artifacts);
  for (const s of targets) {
    const current = lock.artifacts[s];
    if (!current) throw new CliError(`Non installé : ${s}.`);
    const ref = parseSlug(s)!;
    const latest = await ctx.api.byName(ref.ns, ref.name);
    if (!latest || latest.contentHash === current.hash) { ctx.out(`${s} déjà à jour.`); continue; }
    const abs = absolute(ctx, current);
    removeInstalled(abs.files, abs.merged);
    const entry = await install(ctx, s, { ...o, agent: str(o, 'agent') ?? current.agent, g: global });
    lock.artifacts[s] = entry;
    ctx.out(`Mis à jour ${s} : ${current.version} → ${entry.version}.`);
  }
  writeLock(lf, lock);
  return 0;
}

export function cmdPack(ctx: Ctx, target: string | undefined, o: Opts): number {
  const src = target ?? '.';
  const files = readArtifact(path.resolve(ctx.cwd, src));
  const type = asType(str(o, 'type')) ?? detectType(Object.keys(files));
  if (!type) throw new CliError('Type non détecté : précisez --type.');
  const name = str(o, 'name') ?? path.basename(path.resolve(ctx.cwd, src)).replace(/\.(agent|prompt)\.md$/, '');
  const out = path.resolve(ctx.cwd, str(o, 'output') ?? `${name}.zip`);
  fs.writeFileSync(out, toZip(files));
  ctx.out(`Archive ${type} écrite : ${out} (${Object.keys(files).length} fichier(s)).`);
  return 0;
}

function printIssues(ctx: Ctx, label: string, issues: { code: string; message: string; file?: string; line?: number }[]) {
  for (const i of issues) ctx.err(`${label} ${i.code}: ${i.message}${i.file ? ` (${i.file}${i.line ? ':' + i.line : ''})` : ''}`);
}

export async function cmdCheck(ctx: Ctx, target: string | undefined, o: Opts): Promise<number> {
  const src = path.resolve(ctx.cwd, target ?? '.');
  const files = readArtifact(src);
  const type = asType(str(o, 'type')) ?? detectType(Object.keys(files));
  if (!type) throw new CliError('Type non détecté : précisez --type.');
  const name = str(o, 'name') ?? path.basename(src).replace(/\.(agent|prompt)\.md$/, '');
  const ns = str(o, 'namespace') ?? 'myregistry';
  let blocking = 0;

  const names = checkName(ns, name, type);
  printIssues(ctx, 'ERREUR', names.issues);
  if (names.canonical) ctx.err(`Forme canonique proposée : ${names.canonical}`);
  blocking += names.issues.length;

  const main = mainFileFor(type, Object.keys(files));
  if (main) {
    const additional: Record<string, string> = {};
    const binary: Record<string, string> = {};
    for (const [p, d] of Object.entries(files)) {
      if (p === main) continue;
      if (isBinary(d)) binary[p] = Buffer.from(d).toString('base64'); else additional[p] = Buffer.from(d).toString('utf8');
    }
    try {
      const r = await ctx.api.validate({ type, fileName: main, content: Buffer.from(files[main]).toString('utf8'), additionalFiles: additional, binaryFiles: binary });
      printIssues(ctx, 'ERREUR', r.errors);
      printIssues(ctx, 'AVERT.', r.warnings);
      blocking += r.errors.length;
    } catch (e) {
      if (!(e instanceof CliError)) throw e;
      ctx.err(`AVERT. contrôle serveur ignoré : ${e.message}`);
    }
  }
  ctx.out(blocking === 0 ? `OK : ${type} ${ns}/${name}` : `${blocking} problème(s) bloquant(s).`);
  return blocking === 0 ? 0 : 1;
}

export async function cmdPublish(ctx: Ctx, target: string | undefined, o: Opts): Promise<number> {
  const src = path.resolve(ctx.cwd, target ?? '.');
  const files = readArtifact(src);
  const type = asType(str(o, 'type') ?? str(o, 't')) ?? detectType(Object.keys(files));
  if (!type) throw new CliError('Type non détecté : précisez --type.');
  const main = mainFileFor(type, Object.keys(files));
  if (!main || !files[main]) throw new CliError('Fichier principal introuvable.');
  const name = str(o, 'name') ?? path.basename(src).replace(/\.(agent|prompt)\.md$/, '');
  const ns = str(o, 'namespace') ?? (await ctx.api.registryInfo()).defaultNamespace ?? 'myregistry';
  const requested = str(o, 'version');
  const version = requested ?? (await ctx.api.suggestedVersion(ns, name, type));

  const additional: Record<string, string> = {};
  const binary: Record<string, string> = {};
  for (const [p, d] of Object.entries(files)) {
    if (p === main) continue;
    if (isBinary(d)) binary[p] = Buffer.from(d).toString('base64'); else additional[p] = Buffer.from(d).toString('utf8');
  }
  const body = {
    type, namespace: ns, name, version, description: str(o, 'description'),
    fileName: main, rawContent: Buffer.from(files[main]).toString('utf8'),
    tags: str(o, 'tags')?.split(',').map((t) => t.trim()).filter(Boolean),
    additionalFiles: additional, binaryFiles: binary,
  };

  if (o['dry-run']) {
    const r = await ctx.api.validate({ type, fileName: main, content: body.rawContent, additionalFiles: additional, binaryFiles: binary });
    printIssues(ctx, 'ERREUR', r.errors);
    printIssues(ctx, 'AVERT.', r.warnings);
    ctx.out(`${r.isValid ? 'Validation OK' : 'Validation en échec'} (dry-run) : ${ns}/${name}@${version}, grade ${r.grade}. Rien n'a été publié.`);
    return r.isValid ? 0 : 1;
  }

  const { ok, result } = await ctx.api.importArtifact(body);
  if (!ok) {
    printIssues(ctx, 'ERREUR', result.errors ?? []);
    if (result.canonicalName) ctx.err(`Forme canonique proposée : ${result.canonicalName}`);
    ctx.err(result.title ?? 'Publication refusée.');
    return 1;
  }
  printIssues(ctx, 'AVERT.', result.warnings ?? []);
  ctx.out(`Publié ${ns}/${name}@${result.version} (Pending, grade ${result.grade}). En attente de modération.`);
  return 0;
}

export function buildCtx(base: { cwd: string; home: string; env?: NodeJS.ProcessEnv }, out: (s: string) => void, err: (s: string) => void, fetchImpl?: typeof fetch): Ctx {
  const config = resolveConfig({ env: base.env, cwd: base.cwd, home: base.home });
  return { cwd: base.cwd, home: base.home, config, api: new Api(config.baseUrl, config.apiToken, fetchImpl), out, err };
}
