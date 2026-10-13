import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { resolveConfig, writeConfig, DEFAULT_BASE_URL } from '../src/config.js';
import { tmp } from './helpers.js';

test('défaut quand rien n\'est configuré', () => {
  const d = tmp();
  assert.equal(resolveConfig({ env: {}, cwd: d, home: d }).baseUrl, DEFAULT_BASE_URL);
});

test('cascade : env > projet (parent) > global > défaut', () => {
  const home = tmp();
  const proj = tmp();
  const sub = path.join(proj, 'a', 'b');
  fs.mkdirSync(sub, { recursive: true });
  writeConfig(path.join(home, '.myregistry', 'config.json'), { registryBaseUrl: 'http://global' });
  assert.equal(resolveConfig({ env: {}, cwd: sub, home }).baseUrl, 'http://global');
  writeConfig(path.join(proj, '.myregistry.json'), { registryBaseUrl: 'http://project/' });
  assert.equal(resolveConfig({ env: {}, cwd: sub, home }).baseUrl, 'http://project'); // remonte les parents, retire le « / » final
  assert.equal(resolveConfig({ env: { MYREGISTRY_BASE_URL: 'http://env' }, cwd: sub, home }).baseUrl, 'http://env');
});

test('le token vient seulement de l\'environnement', () => {
  const d = tmp();
  writeConfig(path.join(d, '.myregistry.json'), { apiToken: 'leak' });
  assert.equal(resolveConfig({ env: {}, cwd: d, home: d }).apiToken, undefined);
  assert.equal(resolveConfig({ env: { MYREGISTRY_API_TOKEN: 't' }, cwd: d, home: d }).apiToken, 't');
});

test('writeConfig conserve les clés existantes', () => {
  const d = tmp();
  const f = path.join(d, 'c.json');
  writeConfig(f, { a: 1 });
  writeConfig(f, { b: 2 });
  assert.deepEqual(JSON.parse(fs.readFileSync(f, 'utf8')), { a: 1, b: 2 });
});
