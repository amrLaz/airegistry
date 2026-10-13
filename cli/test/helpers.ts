import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export function tmp(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'myregistry-cli-'));
}
export const enc = (s: string) => new TextEncoder().encode(s);
