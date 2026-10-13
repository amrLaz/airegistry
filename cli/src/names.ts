import type { ArtifactType } from './types.js';

const FORMAT = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const RESERVED: Partial<Record<ArtifactType, string[]>> = { Skill: ['synced'], Agent: ['default', 'worker', 'explorer'] };
const TYPE_WORD: Record<ArtifactType, string> = {
  McpServer: 'mcp', Skill: 'skill', Agent: 'agent', Prompt: 'prompt', Plugin: 'plugin', Hook: 'hook',
};

export interface NameIssue { code: string; message: string }

export function canonicalize(s: string): string {
  return s.trim().normalize('NFD').replace(/\p{Mn}/gu, '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

/** Port de NameRules (docs/05) pour le contrôle local sans serveur. */
export function checkName(ns: string, name: string, type: ArtifactType, creation = true): { issues: NameIssue[]; canonical?: string } {
  const issues: NameIssue[] = [];
  if (ns.length < 2 || ns.length > 32) issues.push({ code: 'ns-length', message: 'Le namespace doit faire 2 à 32 caractères.' });
  if (!FORMAT.test(ns)) issues.push({ code: 'ns-format', message: 'Namespace : minuscules ASCII, chiffres, tirets simples.' });
  if (name.length < 2 || name.length > 48) issues.push({ code: 'name-length', message: 'Le nom doit faire 2 à 48 caractères.' });
  const formatOk = FORMAT.test(name);
  if (!formatOk) issues.push({ code: 'name-format', message: 'Nom : minuscules ASCII, chiffres, tirets simples.' });
  if (ns.length + 1 + name.length > 64) issues.push({ code: 'combined-length', message: 'La forme namespace-nom dépasse 64 caractères.' });
  if (RESERVED[type]?.includes(name)) issues.push({ code: 'reserved-name', message: `Nom réservé : ${name}.` });
  if (['-gh', '-azd', '-feed'].some((s) => name.endsWith(s))) issues.push({ code: 'forbidden-suffix', message: 'Suffixe interdit (-gh, -azd, -feed).' });
  if (creation && name.split('-').includes(TYPE_WORD[type])) issues.push({ code: 'name-contains-type', message: 'Le nom ne doit pas contenir son type.' });
  return { issues, canonical: formatOk ? undefined : canonicalize(name) };
}

export function parseSlug(slug: string): { ns: string; name: string } | undefined {
  const m = /^([^/\s]+)\/([^/\s]+)$/.exec(slug);
  return m ? { ns: m[1], name: m[2] } : undefined;
}
