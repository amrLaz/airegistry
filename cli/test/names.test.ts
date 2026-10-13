import test from 'node:test';
import assert from 'node:assert/strict';
import { checkName, canonicalize, parseSlug } from '../src/names.js';

test('noms valides', () => assert.equal(checkName('myregistry', 'pdf-extractor', 'Skill').issues.length, 0));

for (const [ns, name, type, code] of [
  ['a', 'pdf-tool', 'Skill', 'ns-length'],
  ['myregistry', 'x', 'Skill', 'name-length'],
  ['myregistry', 'Bad_Name', 'Skill', 'name-format'],
  ['myregistry', 'synced', 'Skill', 'reserved-name'],
  ['myregistry', 'worker', 'Agent', 'reserved-name'],
  ['myregistry', 'tool-gh', 'Skill', 'forbidden-suffix'],
  ['myregistry', 'my-skill', 'Skill', 'name-contains-type'],
] as const) {
  test(`refus ${code} (${name})`, () => assert.ok(checkName(ns, name, type).issues.some((i) => i.code === code)));
}

test('worker autorisé pour un prompt', () => assert.equal(checkName('myregistry', 'worker', 'Prompt').issues.length, 0));
test('forme canonique proposée', () => assert.equal(checkName('myregistry', 'Pdf Extractor_v2', 'Skill').canonical, 'pdf-extractor-v2'));
test('canonicalize retire les accents', () => assert.equal(canonicalize('Sécurité'), 'securite'));
test('parseSlug', () => {
  assert.deepEqual(parseSlug('myregistry/pdf'), { ns: 'myregistry', name: 'pdf' });
  assert.equal(parseSlug('pdf'), undefined);
  assert.equal(parseSlug('a/b/c'), undefined);
});
