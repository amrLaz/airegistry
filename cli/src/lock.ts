import fs from 'node:fs';
import path from 'node:path';
import type { Agent, ArtifactType } from './types.js';

export interface LockEntry {
  id: string;
  version: string;
  hash: string;
  type: ArtifactType;
  agent: Agent;
  global: boolean;
  files: string[];
  /** Entrées JSON fusionnées (mcp) à retirer lors de la suppression. */
  merged?: { path: string; topKey: string; entry: string }[];
}

export interface Lock { version: 1; artifacts: Record<string, LockEntry> }

export function lockPath(global: boolean, cwd: string, home: string): string {
  return global ? path.join(home, '.myregistry', 'myregistry.lock') : path.join(cwd, 'myregistry.lock');
}

export function readLock(file: string): Lock {
  try {
    const l = JSON.parse(fs.readFileSync(file, 'utf8')) as Lock;
    return { version: 1, artifacts: l.artifacts ?? {} };
  } catch {
    return { version: 1, artifacts: {} };
  }
}

export function writeLock(file: string, lock: Lock): void {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const sorted = Object.fromEntries(Object.entries(lock.artifacts).sort(([a], [b]) => a.localeCompare(b)));
  fs.writeFileSync(file, JSON.stringify({ version: 1, artifacts: sorted }, null, 2) + '\n');
}
