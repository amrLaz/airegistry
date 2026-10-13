# 02 — Architecture

## Vue d'ensemble
```
[CLI myregistry] ──HTTP──► [MyRegistry.Web : API + Blazor Server]
[Navigateur]     ──HTTP──►        │
                                  ├── PostgreSQL (données, contenu des versions)
                                  ├── IBlobStore (local → Azure Blob)
                                  ├── IGitProvider (AzureDevOps RW, GitHub RO)
                                  ├── IScanner (SkillSpector ou équivalent)
                                  └── Background jobs (verrou advisory lock)
```

## Structure de solution
```
src/
  MyRegistry.Domain/          entités, enums, règles pures
  MyRegistry.Application/     cas d'usage, validation, scoring, services
  MyRegistry.Infrastructure/  EF Core, Git providers, blob store, scanner
  MyRegistry.Web/             API minimale + Blazor + auth + OpenAPI
tests/
  MyRegistry.Domain.Tests/
  MyRegistry.Integration.Tests/   Testcontainers PostgreSQL
cli/                          npm, TypeScript
docs/
Dockerfile
docker-compose.yml
```

## Modèle de données (PostgreSQL)
- `artifacts` (id, namespace, name, type, created_at) unique(namespace, name, type)
- `artifact_versions` (id, artifact_id, version, status, quarantined, content_hash, quality_grade, origin [Internal|External], feed_id, repo_url, git_path, commit_sha, pr_url, created_by, created_at) unique(artifact_id, version)
- `version_files` (version_id, path, content bytea/text, is_binary) : contenu complet en base
- `tags` / `artifact_tags` (partagés par famille)
- `votes` (artifact_id, user_id), `downloads` (version_id, count)
- `reports` (version_id, user_id, reason, details, status), `comments` (+ modération)
- `status_history` (version_id, from, to, user_id, at, note)
- `governance` (artifact_id, owner_id, risk_level, data_class, review_every_days, last_reviewed, capabilities[])
- `plugin_components` (plugin_version_id, type, slug, exact_version, resolved)
- `security_scans` (version_id, content_hash, mode, tool_version, started, ended, max_severity, coverage, verdict, incomplete)
- `security_findings` (scan_id, rule_id, severity, file, line, message, blocks_publish)
- `security_waivers` (version_id, content_hash, accepted_severity, by, at, expires_at, reason, revoked_at)
- `scan_baseline` (yaml, sha256, excluded_rules[], updated_by, at, active)
- `feeds` (id, name, type [AzureDevOps|GitHub], url, branch, pat_ref, interval_min, active, is_system, namespace_override, status, last_sync, consecutive_failures) + `feed_sync_reports`
- `namespace_aliases` (old_slug, new_slug, expires_at)
- `users` (object_id, email, display_name, role, active, last_login)
- `notifications`, `notification_preferences`, `notification_templates` (phase 8)
- `audit_log` (at, user_id, action, entity, entity_id, details jsonb)
- `settings` (default_namespace, title, public_url, auto_publish_marketplace, publish_interval_h, theme)
- `cli_tokens` (court, non persisté côté CLI)

## Enums
`ArtifactType`: McpServer, Skill, Agent, Prompt, Plugin, Hook
`VersionStatus`: Pending, Approved, ToReview, Deprecated
`Role`: Reader, Publisher, Moderator, SecurityValidator, Admin
`RiskLevel`, `DataClass`: à définir (Low/Medium/High/Critical ; Public/Internal/Confidential/Restricted)
Dans l'API, sérialiser les enums en chaînes (pas en entiers).

## Jobs de fond
| Job | Fréquence | Remarque |
|---|---|---|
| Feed sync | par feed (60 / 1440 min) | delta par dernier commit traité ; pause après échecs consécutifs |
| Publication snapshot marketplace | 6 h (config) | ne pousse rien si digest inchangé |
| Vérifications d'échéance | 5 min | |
| Cache marketplace.json | 5 min | invalidé aux changements de visibilité |
Chaque job prend un `pg_try_advisory_lock` avant de s'exécuter.

## Auth
`IAuthProvider` : `dev` (utilisateur factice) et `oidc` (Entra ID). Rôles stockés en base, attribués par Admin. Jeton CLI : navigateur → `/cli/auth` (callback local + state), validité 1 minute, jamais persisté ; `MYREGISTRY_API_TOKEN` pour la CI.

## Intégrité
SHA-256 sur tout contenu servi ; en-têtes `X-Artifact-Sha256` et `ETag` sur les téléchargements.

## Conteneur
Image multi-stage, utilisateur non-root, port 8080, `/health` pour liveness/readiness, aucune écriture hors `/data` (jetable). Config 12-factor par variables `MYREGISTRY_*`.

## Cible cloud (phase 7)
Azure Container Apps, PostgreSQL Flexible Server, Key Vault (identité managée), Blob, ACR, pipeline Azure DevOps, révisions ACA pour le double run. Blazor Server : activer l'affinité de session.
