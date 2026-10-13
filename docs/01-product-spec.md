# 01 — Spécification produit

## Objectif
Registre interne pour découvrir, installer, publier et gouverner des artefacts IA. Consultable anonymement, actions protégées par rôle.

## Types d'artefacts
| Type | Fichier clé | Notes |
|---|---|---|
| McpServer | `mcp.json` ou `package.json` | Transports : stdio, docker, npx, http/sse, uvx (détection auto) |
| Skill | dossier avec `SKILL.md` | Frontmatter YAML `name`, `description` |
| Agent | `*.agent.md` ou `AGENTS.md` | Frontmatter recommandé |
| Prompt | `*.prompt.md` | Frontmatter `agent`, `description` |
| Plugin | `plugin.json` dans le dossier runtime (`.claude-plugin`, `.github/plugin`, `.codex-plugin`) | Bundle d'artefacts, SemVer exact par composant |
| Hook | `hooks.json` + `README.md` | Pour Claude Code et Copilot |

Le type est détecté depuis l'extension/nom de fichier.

## Identité d'un artefact
`namespace/name` (slug) + version SemVer. Une « famille » = toutes les versions d'un slug. Le catalogue montre la dernière version approuvée visible, hors quarantaine.

## Statuts d'une version
`Pending` → `Approved` | `Deprecated` ; `ToReview` (finding de sécurité tardif) ; la quarantaine est un indicateur distinct (≥ 5 signalements en 1 h, ou secret sur import externe).

## Rôles
| Rôle | Droits |
|---|---|
| Anonyme | consulter, inspecter, exporter |
| Reader | + upvote, fork, signaler, commenter |
| Publisher | + publier (Pending), éditeur de plugins, Mes Publications |
| Moderator | + approuver/rejeter, relancer scan, dérogation High |
| SecurityValidator | idem Moderator + dérogation Critical + contrat de gouvernance |
| Admin | tout + feeds, config, webhooks, utilisateurs, audit |

## Fonctionnalités par écran
- **Catalogue** : hub par famille, recherche (nom/auteur/description), tags (ET), tri (récent/populaire), badges de source.
- **Fiche artefact** : identité, description, SHA-256, grade qualité A/B/C, contenu brut, sécurité et provenance, contrat de gouvernance, composition (plugins), versions, tags, commentaires, historique, actions d'export (copier brut, `.cursorrules`, config Claude Desktop, prompt d'audit LLM), upvote/fork/signaler.
- **Mes Publications** : soumissions par statut, publier (archive ou fichier), unpublish (→ Deprecated).
- **Éditeur de plugins** : composer un bundle, badge « mise à jour disponible », export/réimport ZIP.
- **Admin** : tableau de bord (KPI, répartition par type, activité récente), modération (filtres Tous/En attente/Approuvés/Dépréciés/À revoir, Réparer), scans de sécurité, signalements, Sanity Check, journal d'audit, utilisateurs, notifications, configuration, feeds.
- **Recherche globale** (Ctrl+K) : artefacts approuvés + rubriques de doc.

## Gouvernance
Contrat par artefact : propriétaire, niveau de risque, classe de données, fréquence de revue, capabilities. Modifiable par auteur, SecurityValidator ou Admin.

## Publication
Voies : CLI `publish`, API `POST /api/import`, interface web, commit dans un dépôt synchronisé (feed). Toute première publication interne ouvre une PR/MR sur le dépôt de gouvernance et n'aboutit que si la PR est créée. `edit` crée une version Pending directement par API, sans PR.

## Feeds (périmètre initial)
1. Dépôt Azure DevOps « système » (gouvernance, lecture/écriture), sync 60 min.
2. Un dépôt GitHub externe (lecture seule), sync 1440 min.
Marqués Externe ; import ≠ publication (peut rester ToReview/quarantaine).

## Marketplace
Manifeste HTTP dynamique `/marketplace.json` (plugins approuvés non quarantinés, cache 5 min) + snapshot Git `.github/plugin/marketplace.json` publié dans le dépôt par défaut (VS Code lit un repo Git, pas un endpoint JSON).

## Sécurité
Scan statique (SkillSpector ou équivalent) à chaque publication interne. Findings High/Critical bloquent l'approbation, y compris forcée. Dérogation par version + hash de contenu, motif obligatoire, expiration facultative. Baseline YAML v2 globale avec règles exclues. Backfill sur toutes les dernières versions. Finding tardif → ToReview.

## Hors périmètre initial
GitLab, garde-fous de poste, cockpit, notifications externes, serveur MCP.
