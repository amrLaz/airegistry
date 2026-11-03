import http from 'node:http';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { CliError } from './types.js';

export interface Io {
  /** Ouvre une URL dans le navigateur. */
  openUrl: (url: string) => void | Promise<void>;
  /** Ouvre un dossier dans l'éditeur et rend la main quand l'utilisateur a fini. */
  edit: (dir: string, editor?: string) => Promise<void>;
  timeoutMs: number;
}

export const defaultIo: Io = {
  timeoutMs: 120_000,
  openUrl(url) {
    const [cmd, args] = process.platform === 'darwin' ? ['open', [url]] : process.platform === 'win32' ? ['cmd', ['/c', 'start', '', url]] : ['xdg-open', [url]];
    spawn(cmd, args, { stdio: 'ignore', detached: true }).on('error', () => { /* l'URL est aussi affichée */ }).unref();
  },
  async edit(dir, editor) {
    const cmd = editor ?? process.env.VISUAL ?? process.env.EDITOR ?? 'code';
    await new Promise<void>((resolve, reject) => {
      const p = spawn(cmd, cmd === 'code' ? ['--wait', dir] : [dir], { stdio: 'inherit', shell: process.platform === 'win32' });
      p.on('error', () => reject(new CliError(`Éditeur introuvable : ${cmd} (voir --editor ou $EDITOR).`)));
      p.on('exit', () => resolve());
    });
  },
};

/**
 * Flux navigateur (docs/04) : serveur local éphémère → /cli/auth?callback&state → le registre redirige avec le jeton (valable 1 minute).
 * Le jeton reste en mémoire : il n'est jamais écrit sur disque.
 */
export async function browserLogin(baseUrl: string, io: Io, say: (s: string) => void): Promise<string> {
  const state = crypto.randomBytes(24).toString('base64url');
  let server!: http.Server;
  const token = new Promise<string>((resolve, reject) => {
    server = http.createServer((req, res) => {
      const u = new URL(req.url ?? '/', 'http://localhost');
      if (u.pathname !== '/callback') { res.writeHead(404).end(); return; }
      const got = u.searchParams.get('token');
      if (u.searchParams.get('state') !== state || !got) { res.writeHead(400).end('Requête invalide.'); return; }
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }).end('<p>Connexion réussie. Vous pouvez fermer cet onglet.</p>');
      resolve(got);
    });
    server.on('error', reject);
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const port = (server.address() as { port: number }).port;
  const url = `${baseUrl.replace(/\/$/, '')}/cli/auth?callback=${encodeURIComponent(`http://localhost:${port}/callback`)}&state=${state}`;
  say(`Ouverture du navigateur… Si rien ne s'ouvre, allez sur :\n${url}`);
  try {
    await io.openUrl(url);
    return await Promise.race([
      token,
      new Promise<never>((_, rej) => setTimeout(() => rej(new CliError('Connexion expirée : aucune réponse du navigateur.')), io.timeoutMs).unref()),
    ]);
  } finally {
    server.close();
    server.closeAllConnections?.();
  }
}
