#!/usr/bin/env node
import { parseArgs } from 'node:util';
import os from 'node:os';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import * as c from './commands.js';
import { CliError } from './types.js';

const HELP = `myregistry — registre d'artefacts IA

Usage : myregistry <commande> [options]

  config [--global] [--registry-base-url URL]   affiche / écrit la configuration
  list [-g] | list -r [--type T --search S --tag T --page N --page-size N]
  add <namespace/nom> [--agent copilot|claude|codex] [-g] [--version V]
  remove <nom> [-g]
  outdated [-g] [--all] [--type T]              code de sortie 1 si mise à jour disponible
  update [namespace/nom] [-g]
  pack [chemin] [--type T --name N --output F]
  check [chemin] [--namespace NS --name N --type T]
  publish [chemin] [--dry-run] [--name N] [--namespace NS] [--version V] [--description D] [--tags a,b] [--type T]
  version | help

Variables : MYREGISTRY_BASE_URL, MYREGISTRY_API_TOKEN
Non implémenté pour l'instant : login, edit, add-registry, completion.`;

export async function main(argv: string[], deps?: { cwd?: string; home?: string; env?: NodeJS.ProcessEnv; out?: (s: string) => void; err?: (s: string) => void; fetch?: typeof fetch }): Promise<number> {
  const out = deps?.out ?? ((s: string) => console.log(s));
  const err = deps?.err ?? ((s: string) => console.error(s));
  const [cmd, ...rest] = argv;
  try {
    const { values, positionals } = parseArgs({
      args: rest,
      allowPositionals: true,
      options: {
        global: { type: 'boolean', short: 'g' }, g: { type: 'boolean' },
        remote: { type: 'boolean', short: 'r' }, all: { type: 'boolean' }, json: { type: 'boolean' },
        'dry-run': { type: 'boolean' }, agent: { type: 'string' }, type: { type: 'string', short: 't' },
        search: { type: 'string' }, tag: { type: 'string' }, page: { type: 'string' }, 'page-size': { type: 'string' },
        version: { type: 'string', short: 'v' }, name: { type: 'string', short: 'n' }, namespace: { type: 'string' },
        description: { type: 'string', short: 'd' }, tags: { type: 'string' }, output: { type: 'string', short: 'o' },
        'registry-base-url': { type: 'string' },
      },
    });
    const o = values as c.Opts;
    const ctx = c.buildCtx({ cwd: deps?.cwd ?? process.cwd(), home: deps?.home ?? os.homedir(), env: deps?.env }, out, err, deps?.fetch);
    switch (cmd) {
      case undefined: case 'help': case '--help': out(HELP); return 0;
      case 'version': case '--version': {
        const pkg = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'package.json'), 'utf8'));
        out(pkg.version); return 0;
      }
      case 'config': return c.cmdConfig(ctx, o);
      case 'list': return o.remote ? await c.cmdListRemote(ctx, o) : c.cmdListLocal(ctx, o);
      case 'add': if (!positionals[0]) throw new CliError('Usage : myregistry add <namespace/nom>'); return await c.cmdAdd(ctx, positionals[0], o);
      case 'remove': if (!positionals[0]) throw new CliError('Usage : myregistry remove <nom>'); return c.cmdRemove(ctx, positionals[0], o);
      case 'outdated': return await c.cmdOutdated(ctx, o);
      case 'update': return await c.cmdUpdate(ctx, positionals[0], o);
      case 'pack': return c.cmdPack(ctx, positionals[0], o);
      case 'check': return await c.cmdCheck(ctx, positionals[0], o);
      case 'publish': return await c.cmdPublish(ctx, positionals[0], o);
      case 'login': case 'edit': case 'add-registry': case 'completion':
        throw new CliError(`« ${cmd} » n'est pas encore implémentée.`);
      default: throw new CliError(`Commande inconnue : ${cmd}. Voir « myregistry help ».`);
    }
  } catch (e) {
    if (e instanceof CliError) { err(e.message); return e.exitCode; }
    err(e instanceof Error ? e.message : String(e));
    return 2;
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === fs.realpathSync(process.argv[1])) {
  main(process.argv.slice(2)).then((code) => process.exit(code));
}
