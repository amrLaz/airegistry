import fs from 'node:fs';
import path from 'node:path';

export interface Config {
  baseUrl: string;
  apiToken?: string;
}

export const DEFAULT_BASE_URL = 'http://localhost:5108';

interface ResolveOptions {
  env?: NodeJS.ProcessEnv;
  cwd: string;
  home: string;
}

function readJson(file: string): Record<string, unknown> | undefined {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return undefined;
  }
}

/** Ordre : variable d'env → .myregistry.json (remonte les parents) → ~/.myregistry/config.json → défaut. */
export function resolveConfig({ env = process.env, cwd, home }: ResolveOptions): Config {
  let projectFile: Record<string, unknown> | undefined;
  for (let dir = path.resolve(cwd); ; dir = path.dirname(dir)) {
    projectFile = readJson(path.join(dir, '.myregistry.json'));
    if (projectFile || path.dirname(dir) === dir) break;
  }
  const globalFile = readJson(path.join(home, '.myregistry', 'config.json'));

  const baseUrl =
    env.MYREGISTRY_BASE_URL ??
    (projectFile?.registryBaseUrl as string | undefined) ??
    (globalFile?.registryBaseUrl as string | undefined) ??
    DEFAULT_BASE_URL;
  const apiToken = env.MYREGISTRY_API_TOKEN; // jamais lu ni écrit sur disque
  return { baseUrl: baseUrl.replace(/\/+$/, ''), apiToken };
}

export function writeConfig(file: string, patch: Record<string, unknown>): void {
  const current = readJson(file) ?? {};
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ ...current, ...patch }, null, 2) + '\n');
}

export function configPath(global: boolean, cwd: string, home: string): string {
  return global ? path.join(home, '.myregistry', 'config.json') : path.join(cwd, '.myregistry.json');
}
