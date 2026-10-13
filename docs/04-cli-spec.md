# 04 — CLI `myregistry`

Node 20+, TypeScript, publiée sur npm (scope si le nom est pris ; `bin: myregistry`). Client API généré depuis l'OpenAPI.

## Configuration
Ordre de résolution : variable d'env → `.myregistry.json` (remonte les dossiers parents jusqu'à la racine) → `~/.myregistry/config.json` → défaut.

| Commande | Effet |
|---|---|
| `config [--global] [--registry-base-url URL]` | affiche / écrit la config |
| `login` | ouvre le navigateur (flux /cli/auth) ; token 1 min, jamais persisté |
| `add <ns/name> [--agent copilot\|claude\|codex] [-g]` | installe selon le type et la cible ; sinon détecte la cible ; échoue en non interactif si ambigu |
| `add <uuid>` | repli par identifiant |
| `list [-g] [--tag t]` | artefacts installés |
| `list -r [--type T] [--search s] [--tag t] [--page N --page-size N]` | catalogue distant (max 100/page) |
| `outdated [-g] [--all] [--type T]` | compare `myregistry.lock` au registre ; code de sortie 1 si à mettre à jour |
| `update [slug] [-g] [--agent X]` | met à jour |
| `remove <name> [-g]` | supprime |
| `pack [path] [--type] [--namespace] [--output]` | ZIP ; supporte fichiers simples `.agent.md`, `.prompt.md` |
| `check [path] [--namespace --name --origin --policy]` | nommage + secrets en local ; code ≠ 0 seulement si contrôle bloquant |
| `publish [path] [--dry-run] [-n] [--name] [-v] [-d] [--tags] [-t]` | valide puis soumet (Pending) |
| `edit <ns/name> [--version --editor --resume]` | nouvelle version Pending sans PR (auteur ou Admin) |
| `add-registry [--apply] [--marketplace URL] [-g]` | configure le marketplace pour VS Code (+ Claude Code, Codex) |
| `version`, `completion <bash\|powershell>`, `help` | |

Variables : `MYREGISTRY_BASE_URL`, `MYREGISTRY_API_TOKEN`.

## Emplacements d'installation
**Copilot** : skill `.github/skills/{name}/SKILL.md` (global `~/.agents/skills/`) ; prompt `.github/prompts/{slug}.prompt.md` ; agent `.github/agents/{name}.agent.md` (global `~/.agents/agents/`) ; hook `.github/hooks/hooks.json` ; mcp `.vscode/mcp.json`.
**Claude Code** : skill `.claude/skills/{name}/SKILL.md` (global `~/.claude/skills/`) ; agent `.claude/agents/{name}.agent.md` ; hook `.claude/hooks/` (global fusionné dans `~/.claude/settings.json`) ; mcp `.mcp.json` (global `~/.claude.json`).
**Codex** : skill `.codex/skills/{name}/SKILL.md` ; agent `.codex/agents/{name}.toml` ; mcp `.codex/mcp.json`.
Prompts : Copilot seulement. Hooks : Copilot et Claude Code seulement.
**Plugins** : `add` n'installe rien ; assistant qui demande l'agent cible et affiche les commandes natives. `update`/`remove`/`outdated --type Plugin` refusés.

## Authentification
`publish` et `edit` ouvrent le navigateur sans token. Le token n'est jamais écrit sur disque.
