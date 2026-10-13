import fs from 'node:fs';
import path from 'node:path';
import { zipSync, unzipSync } from 'fflate';
import type { ArtifactType } from './types.js';
import type { Files } from './install.js';

const IGNORED = new Set(['node_modules', '.git', 'bin', 'obj', 'dist']);

/** Lit un dossier ou un fichier simple (.agent.md / .prompt.md) en map chemin relatif → octets. */
export function readArtifact(target: string): Files {
  const abs = path.resolve(target);
  const stat = fs.statSync(abs);
  if (stat.isFile()) return { [path.basename(abs)]: new Uint8Array(fs.readFileSync(abs)) };
  const files: Files = {};
  const walk = (dir: string) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (IGNORED.has(e.name)) continue;
      const full = path.join(dir, e.name);
      if (e.isDirectory()) walk(full);
      else files[path.relative(abs, full).split(path.sep).join('/')] = new Uint8Array(fs.readFileSync(full));
    }
  };
  walk(abs);
  return files;
}

const PLUGIN_MANIFESTS = ['.claude-plugin/plugin.json', '.github/plugin/plugin.json', '.codex-plugin/plugin.json'];

export function detectType(paths: string[]): ArtifactType | undefined {
  if (paths.some((p) => PLUGIN_MANIFESTS.includes(p))) return 'Plugin';
  if (paths.includes('hooks.json')) return 'Hook';
  if (paths.includes('SKILL.md')) return 'Skill';
  if (paths.some((p) => p.endsWith('.agent.md') || p === 'AGENTS.md')) return 'Agent';
  if (paths.some((p) => p.endsWith('.prompt.md'))) return 'Prompt';
  if (paths.includes('mcp.json') || paths.includes('package.json')) return 'McpServer';
  return undefined;
}

export function mainFileFor(type: ArtifactType, paths: string[]): string | undefined {
  switch (type) {
    case 'Skill': return 'SKILL.md';
    case 'Hook': return 'hooks.json';
    case 'McpServer': return paths.includes('mcp.json') ? 'mcp.json' : 'package.json';
    case 'Agent': return paths.find((p) => p.endsWith('.agent.md')) ?? 'AGENTS.md';
    case 'Prompt': return paths.find((p) => p.endsWith('.prompt.md'));
    case 'Plugin': return paths.find((p) => PLUGIN_MANIFESTS.includes(p));
  }
}

export function toZip(files: Files): Uint8Array {
  return zipSync(Object.fromEntries(Object.entries(files).map(([k, v]) => [k, [v, { level: 6, mtime: new Date(Date.UTC(1980, 0, 1)) }]])));
}

export function fromZip(data: Uint8Array): Files {
  const out = unzipSync(data);
  for (const k of Object.keys(out)) if (k.endsWith('/') || k.split('/').includes('..')) delete out[k];
  return out;
}

export const isBinary = (data: Uint8Array): boolean => data.includes(0);
