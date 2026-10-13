import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { planInstall, applyPlan, removeInstalled, detectAgent, agentToToml } from '../src/install.js';
import { CliError } from '../src/types.js';
import { tmp, enc } from './helpers.js';

const skill = { 'SKILL.md': enc('---\nname: s\ndescription: d\n---\n# S'), 'README.md': enc('r') };
const agentFiles = { 'rev.agent.md': enc('---\nname: rev\ndescription: Reviewer\n---\nTu relis le code.') };
const mcp = { 'mcp.json': enc('{"name":"a","command":"npx","args":["-y","p"],"env":{"K":"v"}}') };
const rel = (p: { writes: { path: string }[] }, root: string) => p.writes.map((w) => path.relative(root, w.path).split(path.sep).join('/'));

test('skill par agent (projet et global)', () => {
  const cwd = tmp(); const home = tmp();
  assert.deepEqual(rel(planInstall('Skill', 's', 'copilot', false, skill, { cwd, home }), cwd), ['.github/skills/s/SKILL.md', '.github/skills/s/README.md']);
  assert.deepEqual(rel(planInstall('Skill', 's', 'claude', false, skill, { cwd, home }), cwd), ['.claude/skills/s/SKILL.md', '.claude/skills/s/README.md']);
  assert.deepEqual(rel(planInstall('Skill', 's', 'codex', false, skill, { cwd, home }), cwd), ['.codex/skills/s/SKILL.md', '.codex/skills/s/README.md']);
  assert.deepEqual(rel(planInstall('Skill', 's', 'copilot', true, skill, { cwd, home }), home), ['.agents/skills/s/SKILL.md', '.agents/skills/s/README.md']);
  assert.deepEqual(rel(planInstall('Skill', 's', 'claude', true, skill, { cwd, home }), home), ['.claude/skills/s/SKILL.md', '.claude/skills/s/README.md']);
});

test('agent : copilot, claude (.agent.md) et codex (.toml)', () => {
  const cwd = tmp(); const home = tmp();
  assert.deepEqual(rel(planInstall('Agent', 'rev', 'copilot', false, agentFiles, { cwd, home }), cwd), ['.github/agents/rev.agent.md']);
  assert.deepEqual(rel(planInstall('Agent', 'rev', 'claude', false, agentFiles, { cwd, home }), cwd), ['.claude/agents/rev.agent.md']);
  const codex = planInstall('Agent', 'rev', 'codex', false, agentFiles, { cwd, home });
  assert.deepEqual(rel(codex, cwd), ['.codex/agents/rev.toml']);
  const toml = new TextDecoder().decode(codex.writes[0].data);
  assert.match(toml, /name = "rev"/);
  assert.match(toml, /description = "Reviewer"/);
  assert.match(toml, /Tu relis le code\./);
});

test('agentToToml échappe les guillemets triples', () => assert.ok(!agentToToml('a', 'x """ y').includes('x """ y')));

test('prompt : copilot seulement', () => {
  const cwd = tmp(); const home = tmp();
  const f = { 'x.prompt.md': enc('---\nagent: a\ndescription: d\n---') };
  assert.deepEqual(rel(planInstall('Prompt', 'x', 'copilot', false, f, { cwd, home }), cwd), ['.github/prompts/x.prompt.md']);
  assert.throws(() => planInstall('Prompt', 'x', 'claude', false, f, { cwd, home }), CliError);
});

test('hook : copilot et claude seulement', () => {
  const cwd = tmp(); const home = tmp();
  const f = { 'hooks.json': enc('{"hooks":{"A":[]}}'), 'README.md': enc('r') };
  assert.deepEqual(rel(planInstall('Hook', 'h', 'copilot', false, f, { cwd, home }), cwd), ['.github/hooks/hooks.json']);
  assert.deepEqual(rel(planInstall('Hook', 'h', 'claude', false, f, { cwd, home }), cwd), ['.claude/hooks/h/hooks.json', '.claude/hooks/h/README.md']);
  assert.throws(() => planInstall('Hook', 'h', 'codex', false, f, { cwd, home }), CliError);
});

test('mcp : fusion JSON selon l\'agent', () => {
  const cwd = tmp(); const home = tmp();
  const c = planInstall('McpServer', 'srv', 'copilot', false, mcp, { cwd, home }).merges[0];
  assert.equal(path.relative(cwd, c.path), path.join('.vscode', 'mcp.json'));
  assert.equal(c.topKey, 'servers');
  assert.deepEqual(c.value, { command: 'npx', args: ['-y', 'p'], env: { K: 'v' } });
  assert.equal(path.relative(cwd, planInstall('McpServer', 'srv', 'claude', false, mcp, { cwd, home }).merges[0].path), '.mcp.json');
  assert.equal(path.relative(home, planInstall('McpServer', 'srv', 'claude', true, mcp, { cwd, home }).merges[0].path), '.claude.json');
  assert.equal(path.relative(cwd, planInstall('McpServer', 'srv', 'codex', false, mcp, { cwd, home }).merges[0].path), path.join('.codex', 'mcp.json'));
});

test('plugins refusés par add', () => assert.throws(() => planInstall('Plugin', 'p', 'claude', false, {}, { cwd: tmp(), home: tmp() }), /plugins/i));

test('applyPlan fusionne sans écraser les autres serveurs, removeInstalled les retire', () => {
  const cwd = tmp(); const home = tmp();
  fs.writeFileSync(path.join(cwd, '.mcp.json'), JSON.stringify({ mcpServers: { other: { command: 'x' } } }));
  const done = applyPlan(planInstall('McpServer', 'srv', 'claude', false, mcp, { cwd, home }));
  const after = JSON.parse(fs.readFileSync(path.join(cwd, '.mcp.json'), 'utf8'));
  assert.deepEqual(Object.keys(after.mcpServers).sort(), ['other', 'srv']);
  removeInstalled(done.files, done.merged);
  assert.deepEqual(Object.keys(JSON.parse(fs.readFileSync(path.join(cwd, '.mcp.json'), 'utf8')).mcpServers), ['other']);
});

test('removeInstalled supprime les fichiers et les dossiers vides', () => {
  const cwd = tmp(); const home = tmp();
  const done = applyPlan(planInstall('Skill', 's', 'claude', false, skill, { cwd, home }));
  assert.ok(fs.existsSync(path.join(cwd, '.claude/skills/s/SKILL.md')));
  removeInstalled(done.files);
  assert.ok(!fs.existsSync(path.join(cwd, '.claude')));
});

test('detectAgent : un seul, aucun, ambigu', () => {
  const d = tmp();
  assert.throws(() => detectAgent(d), /précisez --agent/);
  fs.mkdirSync(path.join(d, '.claude'));
  assert.equal(detectAgent(d), 'claude');
  fs.mkdirSync(path.join(d, '.github'));
  assert.throws(() => detectAgent(d), /ambigu/);
});
