import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { detectType, mainFileFor, readArtifact, toZip, fromZip, isBinary } from '../src/pack.js';
import { tmp, enc } from './helpers.js';

test('aller-retour zip', () => {
  const files = { 'SKILL.md': enc('# x'), 'sub/a.txt': enc('a') };
  const back = fromZip(toZip(files));
  assert.deepEqual(Object.keys(back).sort(), ['SKILL.md', 'sub/a.txt']);
  assert.equal(new TextDecoder().decode(back['sub/a.txt']), 'a');
});

test('le zip est déterministe', () => assert.deepEqual(toZip({ 'a': enc('1') }), toZip({ 'a': enc('1') })));

test('fromZip ignore les chemins hors racine', () => {
  const evil = toZip({ '../evil.txt': enc('x'), 'ok.txt': enc('y') });
  assert.deepEqual(Object.keys(fromZip(evil)), ['ok.txt']);
});

test('détection de type et fichier principal', () => {
  assert.equal(detectType(['SKILL.md']), 'Skill');
  assert.equal(detectType(['x.agent.md']), 'Agent');
  assert.equal(detectType(['.claude-plugin/plugin.json', 'SKILL.md']), 'Plugin');
  assert.equal(detectType(['foo.txt']), undefined);
  assert.equal(mainFileFor('Agent', ['a.agent.md']), 'a.agent.md');
  assert.equal(mainFileFor('McpServer', ['mcp.json']), 'mcp.json');
});

test('readArtifact : dossier (ignore node_modules/.git) et fichier simple', () => {
  const d = tmp();
  fs.mkdirSync(path.join(d, 'node_modules')); fs.writeFileSync(path.join(d, 'node_modules', 'x.js'), '1');
  fs.mkdirSync(path.join(d, '.git')); fs.writeFileSync(path.join(d, '.git', 'HEAD'), '1');
  fs.writeFileSync(path.join(d, 'SKILL.md'), '# s');
  assert.deepEqual(Object.keys(readArtifact(d)), ['SKILL.md']);
  const single = path.join(d, 'r.agent.md');
  fs.writeFileSync(single, '# a');
  assert.deepEqual(Object.keys(readArtifact(single)), ['r.agent.md']);
});

test('isBinary', () => { assert.ok(isBinary(new Uint8Array([1, 0, 2]))); assert.ok(!isBinary(enc('texte'))); });
