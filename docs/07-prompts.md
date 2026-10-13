# 07 — Prompts prêts à coller dans Claude Code

Commence toujours par : « Lis CLAUDE.md puis les documents cités. »

## Phase 0
Lis CLAUDE.md et docs/01 à 06. Réalise la Phase 0 du backlog : solution .NET en couches, tests, Dockerfile multi-stage, docker-compose (db + web), EF Core + PostgreSQL, /health, auth dev par variables d'env, OpenAPI + Scalar, Blazor Server + MudBlazor avec layout responsive, thème clair/sombre et FR/EN. Annonce ton plan, puis exécute. Termine par les commandes pour lancer et vérifier.

## Phase 1 : règles d'abord
Lis docs/05-validation-rules.md. Écris d'abord les tests unitaires du nommage, des noms proches, des secrets, de la validation par type, de la détection de transport MCP, du versionnement et du score qualité. Fais-les échouer, puis implémente le code jusqu'au vert. Commit.

## Phase 1 : modèle et API
Implémente le modèle de données de docs/02 (artifacts, versions, fichiers, tags, votes, reports, comments, status_history, audit_log, settings) et les endpoints Catalogue + Publication de docs/03 (sans Git : stockage direct, statut Pending). Génère les migrations, les tests d'intégration Testcontainers et mets l'OpenAPI à jour.

## Phase 1 : UI
Implémente le catalogue, la fiche artefact, Mes Publications et la publication par fichier en Blazor/MudBlazor, mobile d'abord. Référence visuelle : docs/reference/screenshots. N'utilise aucun nom de l'organisation d'origine.

## Phase 2 : CLI
Dans cli/, crée la CLI TypeScript `myregistry` selon docs/04. Génère le client depuis l'OpenAPI. Implémente config, login, list -r, add, list, remove, check, pack, publish --dry-run, puis lockfile, outdated, update. Tests avec un serveur mocké et un test e2e contre docker compose.

## Phase 3
Implémente la modération, les rôles, l'audit, les signalements avec quarantaine automatique, le contrat de gouvernance, le tableau de bord admin, la configuration et le Sanity Check selon docs/01, 03, 05 et le backlog Phase 3. Tests des transitions de statut et des permissions par rôle.

## Phase 4
Implémente l'éditeur de plugins, /marketplace.json avec cache et invalidation, le téléchargement ZIP et la génération du snapshot Git vers un repo local file:///. Ajoute `add-registry --apply` à la CLI.

## Phase 5
Introduis IGitProvider avec AzureDevOps (lecture/écriture) et GitHub (lecture seule). Implémente publication par PR, feeds, sync delta, pause après échecs, namespace avec aperçu et alias, jobs de fond protégés par advisory lock. Les PAT viennent des variables d'env.

## Phase 6
Introduis IScanner avec une implémentation SkillSpector (ou un faux scanner configurable pour les tests), le blocage High/Critical, les dérogations, la baseline YAML, le backfill et l'export CSV. Tests des règles de docs/05, section Sécurité.

## Phase 7
Ajoute l'auth OIDC Entra ID, IBlobStore Azure Blob, la lecture des secrets depuis Key Vault, l'IaC pour Azure Container Apps et le pipeline Azure DevOps. Écris le script de migration qui importe les artefacts existants et réécrit les slugs vers myregistry/*, avec alias.

## Revue de fin de tranche (à répéter)
Relis le diff, vérifie : tests verts, OpenAPI à jour, aucun identifiant interdit (up, upai, upcoop, fabrik), aucun secret, aucune écriture hors /data. Mets à jour docs/DECISIONS.md.
