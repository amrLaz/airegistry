# 03 — Contrat d'API

Base : `{MYREGISTRY_PUBLIC_URL}`. JSON, enums en chaînes. Doc interactive : `/scalar/v1`. Accès : A = anonyme, R = Reader+, P = Publisher/Admin (token CLI), M = Moderator/SecurityValidator/Admin, S = SecurityValidator/Admin, Ad = Admin.

## Système
| Méthode | Route | Accès | Rôle |
|---|---|---|---|
| GET | /health | A | état instance + base |
| GET | /api/registry-info | A | namespace par défaut, URL publique, dépôt marketplace |
| GET | /auth/login, /auth/logout | A | SSO |
| GET | /culture/set?culture&redirectUri | A | langue FR/EN |
| GET | /cli/auth?callback&state | A | flux de jeton CLI |

## Catalogue
| Méthode | Route | Accès | Rôle |
|---|---|---|---|
| GET | /api/catalog?type&status&tag&q&origin&feedId&page=1&pageSize=20 | A | liste paginée |
| GET | /api/catalog/{id} | A | détail version |
| GET | /api/catalog/{id}/download | A | contenu |
| GET | /api/catalog/{id}/archive?target&scope | A | archive installable (target: copilot/claude/codex ; scope: local/global) |
| GET | /api/catalog/by-hash/{hash}/download | A | par SHA-256 |
| GET | /api/artifacts/{id}/raw | A | brut par UUID (repli CLI) |
| GET | /api/artifacts/by-name/{namespace}/{name}/raw?version | A | brut par slug (appel de `add`) |
| GET | /api/artifacts/by-name/{name}/raw | A | brut par nom seul |
| GET | /api/catalog/capabilities-inventory | R | union des capabilities |
| POST/DELETE | /api/catalog/{id}/upvote | R | vote / retrait |
| POST | /api/catalog/{id}/report {reason, details} | R | signalement |
| PUT | /api/catalog/{id}/status {status} | M | approuver/rejeter/etc. |
| POST | /api/catalog/{id}/governance | P/S | contrat de gouvernance |
| GET | /api/catalog/{id}/export/cursorrules | A | texte |
| GET | /api/catalog/{id}/export/claude-desktop | A | JSON config MCP |
| GET | /api/catalog/{id}/audit-prompt | A | méta-prompt d'audit |

## Publication
| Méthode | Route | Accès | Rôle |
|---|---|---|---|
| POST | /api/validate {type, fileName, content, additionalFiles, binaryFiles} | P | validation sans publier |
| POST | /api/import (ImportItemRequest) | P | publier (PR de gouvernance) |
| GET | /api/cli/publish/suggested-version?ns&name&type&requestedVersion | P | version suivante |
| GET | /api/cli/edit/source?ns&name&version | P | sources à éditer |
| GET | /api/cli/edit/archive/{id} | P | archive à éditer |

ImportItemRequest : type, namespace, name, version, description, fileName, rawContent, rawMetadataJson?, tags[]?, repositoryUrl?, gitFilePath?, gitBranch?, commitSha?, gitTag?, additionalFiles{path:text}?, binaryFiles{path:base64}?, sourceArtifactId?, sourceVersion?, sourceBaselineHash?

## Plugins et marketplace
| GET | /marketplace.json | A | manifeste dynamique (source `archive` + URL HTTPS) |
| GET | /api/plugins/{ns}/{name}/download | A | ZIP du plugin |

## Surface npm (A)
| GET | /skills/@{scope}/{name} | métadonnées package |
| GET | /skills/@{scope}/{name}/-/{fileName} | tarball |

## Feeds / namespace (Ad)
POST /api/feeds/{id}/namespace/preview, PUT /api/feeds/{id}/namespace  `{namespaceOverride}`. Les anciens slugs restent alias 547 jours.

## Admin
GET /api/audit (Ad) ; GET /api/admin/security-scans/backfill/{runId}/report.csv (Ad).
Pages Blazor : /admin, /admin/moderation, /admin/security-scans, /admin/reports, /admin/sanity, /admin/audit, /admin/users, /admin/notifications, /admin/config, /admin/feeds.

## Notifications (R, phase 8)
GET /api/notifications?page&pageSize ; GET /unread-count ; PATCH /{id}/read ; PATCH /{id}/archive ; DELETE /{id} ; POST /mark-all-read, /archive-all, /delete-all ; GET/PATCH /preferences {eventCode, channel, isEnabled}.

## Hors périmètre initial (phase 8)
/api/guardrails/traces, /api/cockpit/*, /v0/* (compat arctl).

## Erreurs
Problem Details RFC 7807. Rejets de publication : nommer le détecteur et `fichier:ligne`. Nom hors norme : proposer la forme canonique, ne jamais réécrire en silence.
