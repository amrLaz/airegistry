import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** Version de la CLI (package.json) : annoncée au registre dans l'en-tête User-Agent, qui sert à attribuer l'usage à la CLI. */
export const CLI_VERSION: string = (() => {
  try {
    return JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'package.json'), 'utf8')).version as string;
  } catch { return '0.0.0'; }
})();
