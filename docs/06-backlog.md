# 06 — Backlog et critères d'acceptation

Légende : [ ] à faire. Chaque tâche = une tranche verticale avec tests.

## Phase 0 : Socle (1 sem.)
- [ ] Solution .NET (Domain/Application/Infrastructure/Web), tests, Dockerfile multi-stage, docker-compose
- [ ] EF Core + migrations, PostgreSQL, `/health`
- [ ] `IAuthProvider` mode dev (utilisateur + rôle via env)
- [ ] OpenAPI + Scalar, Blazor + MudBlazor, layout responsive, thème clair/sombre, FR/EN
- [ ] CI minimale (build + tests)
**Accepté si** : `docker compose up` affiche une page d'accueil, `/health` = 200, `dotnet test` vert.

## Phase 1 : Noyau (3 sem.)
- [ ] Modèle artefact/version/fichiers/tags/votes + migrations
- [ ] Parsing et détection de type ; validation par type ; nommage ; noms proches ; secrets ; score qualité (tests d'abord, docs/05)
- [ ] `POST /api/validate`, `POST /api/import` (statut Pending, sans Git pour l'instant : stockage direct)
- [ ] Catalogue, détail, download, archive, raw, by-hash, export, audit-prompt, registry-info
- [ ] UI : catalogue (recherche, filtres, tags, tri), fiche artefact, Mes Publications, publication par fichier
- [ ] Upvote, signalement, commentaires
**Accepté si** : publier un skill via l'UI, le voir Pending, l'inspecter, télécharger avec SHA-256 correct.

## Phase 2 : CLI (2 sem.)
- [ ] Client généré depuis l'OpenAPI, config en cascade, `login` (flux /cli/auth)
- [ ] `config`, `list -r`, `add`, `list`, `remove`, `check`, `pack`, `publish [--dry-run]`
- [ ] Emplacements par agent, `myregistry.lock`, `outdated`, `update`
- [ ] Surface npm `/skills/@scope/name`
**Jalon MVP (~6 sem.)** : `publish` puis `add` d'un artefact de bout en bout en local.

## Phase 3 : Gouvernance (3 sem.)
- [ ] Modération : filtres, approuver/rejeter/supprimer, versions, historique de statut, unpublish
- [ ] Rôles et attribution (page Utilisateurs), journal d'audit
- [ ] Signalements, quarantaine auto (≥ 5 en 1 h), déquarantaine
- [ ] Contrat de gouvernance, tableau de bord admin, configuration générale
- [ ] Sanity Check (écarts base / Git / marketplace), action Réparer
- [ ] Suppression de versions avec protections
**Accepté si** : cycle Pending → Approved/Deprecated complet, quarantaine déclenchée par 5 signalements.

## Phase 4 : Plugins et marketplace (3 sem.)
- [ ] Éditeur de plugins (composition SemVer exact, brouillon, publication, badge mise à jour, export/réimport ZIP)
- [ ] `GET /marketplace.json` (cache 5 min, invalidation), `/api/plugins/{ns}/{name}/download`
- [ ] Snapshot Git : génération de `.github/plugin/marketplace.json` (+ `.claude-plugin`, `.agents/plugins`, `.cursor-plugin`) vers un repo local `file:///`
- [ ] CLI `add-registry --apply` (VS Code, Claude Code, Codex)
**Accepté si** : un plugin est installable depuis VS Code via le repo local.

## Phase 5 : Git (3 sem.)
- [ ] `IGitProvider` : AzureDevOps (RW), GitHub (RO)
- [ ] Publication interne par PR (branche `artifact/{ns}/{name}/{version}-{id}`), merge squash à l'approbation, fermeture au rejet, PR vide refusée
- [ ] Feeds : CRUD, test de connexion, sync delta, cache de contenu, rapport de dernière sync, pause auto, suppression avec cascade, namespace avec aperçu et alias 547 j
- [ ] Jobs de fond avec advisory lock ; publication auto du snapshot (6 h, digest)
**Accepté si** : feed GitHub importé en ToReview/Approved selon scan, PR Azure DevOps créée et mergée.

## Phase 6 : Sécurité (2-3 sem.)
- [ ] `IScanner` (SkillSpector ou équivalent), scan direct avant modération
- [ ] Blocage High/Critical, dérogations, révocation, ToReview sur finding tardif
- [ ] Baseline YAML v2, backfill, relance, export CSV, prompt de correction

## Phase 7 : Cloud (2 sem.)
- [ ] OIDC Entra ID, secrets Key Vault, `IBlobStore` Azure Blob
- [ ] IaC (Bicep ou Terraform), ACR, ACA, PostgreSQL Flexible Server, domaine
- [ ] Pipeline Azure DevOps, révisions ACA
- [ ] Migration des artefacts existants + réécriture des slugs vers `myregistry/*` avec alias

## Phase 8 : Optionnel
Notifications in-app, webhooks Teams, garde-fous de poste, cockpit d'orchestration (définir la grille d'étalonnage avant), serveur MCP, plugin Cursor.

## Rétention
Prévoir une politique de rétention des versions (un plugin auto-publié peut atteindre des centaines de versions).
