# Décisions et hypothèses
(Claude Code y consigne chaque hypothèse prise en cas d'ambiguïté : date, décision, raison.)

## 2026-10-13 — Phase 0
- **.NET 10 (LTS)** : conforme à CLAUDE.md. Les packages OpenAPI natifs (`AddOpenApi`) exigent net9+.
- **`/health` = liveness sans dépendance** ; `/health/ready` vérifie la base (utilisé en readiness par ACA plus tard).
- **Config** : `AddEnvironmentVariables("MYREGISTRY_")` ; `MYREGISTRY_DB_CONNECTION` → clé `DB_CONNECTION`.
- **Migrations appliquées au démarrage** (`Seeder`), sauf environnement `Testing`. À revoir en phase 7 (multi-réplicas : advisory lock).
- **Titre et namespace par défaut** : stockés dans la table `settings`, valeurs initiales dans `SettingDefaults` (Application), jamais lus en dur par l'UI.
- **i18n FR/EN** : service `Localizer` maison (dictionnaire) pour la phase 0 ; passage aux `.resx` si le volume de textes augmente.
- **Tests d'intégration phase 0** : `WebApplicationFactory` sans base (env `Testing`). Testcontainers PostgreSQL arrive avec le modèle de données (phase 1).

## 2026-10-13 — Phase 1, règles métier (docs/05)
- **Placement** : règles pures dans `MyRegistry.Application/Rules`, testées dans `MyRegistry.Domain.Tests` (128 tests).
- **Frontmatter** : parseur minimal `clé: valeur` sur une ligne (pas de dépendance YAML). Les listes multi-lignes ne sont pas lues ; seule la présence de la clé compte (`hooks`, `tags`, `tools`, `model`).
- **McpServer via `package.json`** : valide si `name` présent (un paquet npm publié se lance via npx).
- **Grade qualité** : basé sur le nombre d'avertissements (0 → A, 1-2 → B, ≥ 3 → C) ; la nuance « mineurs » de docs/05 n'est pas modélisée.
- **Secrets** : les détecteurs `password` et `api-key` ignorent les valeurs de type placeholder (`your`, `example`, `xxx`...). `dev-port` est un détecteur d'observation et peut doubler `local-loopback-url` : voulu.
- **Noms proches** : normalisation `0→o, 1→l, i→l, 5→s`, tirets ignorés, puis distance Damerau-Levenshtein ≤ 1. Deux noms identiques ne sont pas « proches » (c'est une nouvelle version).
- **Reste de la phase 1** : modèle de données EF Core, endpoints, UI du catalogue (prochaine tranche).

## 2026-10-13 — Phase 1, modèle de données et API (tranche 2)
- **Services EF dans Infrastructure** (`PublishService`, `CatalogService`) : Application ne référence pas Infrastructure. Les DTO et règles restent dans Application.
- **Hash de contenu** : SHA-256 de la liste triée `chemin + SHA-256(fichier)`, indépendant de l'ordre d'envoi. Même valeur en `X-Artifact-Sha256` et `ETag`. Le téléchargement est un ZIP des fichiers.
- **Tests d'intégration** : `MYREGISTRY_TEST_DB` (serveur Postgres existant, utilisé en CI et en local) sinon Testcontainers ; une base éphémère par exécution.
- **Visibilité** : sans rôle Moderator+, le catalogue ne montre que la dernière version `Approved` hors quarantaine. Avec auth dev (Admin), tout est visible, filtrable par `status`.
- **Rejets** : HTTP 422 Problem Details avec `errors` (code, message, fichier:ligne), `warnings` et `canonicalName`.
- **Nom proche entre namespaces** : l'import réussit en `Pending` et l'historique porte la note `name-confusion`.
- **Reste à faire en phase 1** : signalements/commentaires (API), export/audit-prompt, `suggested-version`, UI catalogue/fiche/Mes Publications.

## 2026-10-13 — Mode local SQLite
- Sans `MYREGISTRY_DB_CONNECTION`, l'application démarre sur SQLite (`myregistry-local.db`) pour tourner sans Docker ni Postgres. Schéma créé par `EnsureCreated` (pas de migrations). Réservé au développement : la production et la CI visent PostgreSQL.
- Recherche catalogue rendue portable (`ToLower().Contains`) au lieu de `ILike`.

## 2026-10-13 — UI catalogue (tranche 3)
- Pages Blazor : `/` (catalogue : recherche, filtre par famille, tri, cartes colorées par type), `/artifact/{id}` (fiche : commande d'installation, SHA-256, historique, upvote, approuver/déprécier pour Moderator+), `/publish` (formulaire + chargement de fichier). Maquettes : artifact Design de la session.
- Les composants appellent `CatalogService`/`PublishService` (pas de logique métier dans les composants).
- `MapStaticAssets()` à la place de `UseStaticFiles()`. En local, lancer avec le profil par défaut (`dotnet run`, environnement Development) pour que les assets MudBlazor soient servis.
- Les tags sont désaccentués (`sécurité` → `securite`) par `NameRules.Canonicalize`.
- Reste de la phase 1 : signalements/commentaires, export/audit-prompt, `suggested-version`, page Mes Publications.

## 2026-10-13 — Phase 1, tranche 4 (communauté, exports, Mes Publications)
- **Quarantaine auto** : ≥ 5 signalements dans la dernière heure sur une version (`QuarantinePolicy`). On compte les signalements, pas les utilisateurs distincts (l'auth dev n'a qu'un utilisateur) : à passer en « utilisateurs distincts » avec l'auth OIDC. Un nouveau scan ne lève pas la quarantaine ; seule la levée par un Moderator (`POST /quarantine/release`) le fait.
- **Exports** : `.cursorrules` = fichier principal sans frontmatter ; `claude-desktop` = entrée `mcpServers` (command/args/env/url/transport) uniquement pour les serveurs MCP ; `audit-prompt` = méta-prompt FR avec les fichiers texte inclus.
- **Unpublish** : passe la version en `Deprecated` ; autorisé à l'auteur ou à un Moderator+.
- **Commentaires** : limités à 2000 caractères, rattachés à l'artefact (pas à la version) ; pas encore de modération des commentaires (champ `Hidden` prêt).
- **Fichier SQLite local** : créé dans `src/MyRegistry.Web/` (dossier du projet), pas dans le dossier courant.
- **Phase 1 : reste** : recherche globale (Ctrl+K), `archive` installable, fork, contenu brut sans HTML via `/api/artifacts/{id}/raw`.

## 2026-10-13 — Approche GitOps
- **GitHub Actions** (le dépôt est sur GitHub) à la place d'un pipeline Azure DevOps ; OIDC vers Azure, aucun secret Azure stocké.
- **PostgreSQL Flexible Server** (comme demandé : pas MySQL), Container Apps en mode `Multiple` révisions, `stickySessions` pour Blazor Server.
- **Prod épinglée dans Git** (`prod.bicepparam`) et promue par PR ; **dev** déployée depuis le SHA du commit.
- Bicep validé par `bicep build` uniquement ; non déployé. Voir `docs/08-gitops.md`.

## 2026-10-13 — Phase 2, CLI (tranche 1)
- **Dépendances minimales** : `fflate` pour les ZIP, `node:util.parseArgs` pour les options (pas de framework CLI). Client API écrit à la main (`cli/src/api.ts`) ; la génération depuis l'OpenAPI reste à faire.
- **Non interactif** : `add` n'ouvre aucune invite ; l'agent cible vient de `--agent` ou de la détection (`.claude`, `.github`, `.codex`), échec si absent ou ambigu.
- **Intégrité** : `add` compare `X-Artifact-Sha256` au hash annoncé et n'écrit rien en cas d'écart.
- **Lockfile** : `myregistry.lock` ne contient que des chemins relatifs (versionnable). Global : `~/.myregistry/myregistry.lock`.
- **`check`** : nommage vérifié en local (port TS de NameRules) ; secrets et validation par type via `POST /api/validate` (serveur = source de vérité, évite de dupliquer les détecteurs). Si le registre est injoignable : avertissement, seul le contrôle de nommage s'applique.
- **Ajout API** : `GET /api/catalog/by-name/{ns}/{name}` (dernière version approuvée visible) pour résoudre un slug.
- **Reste phase 2** : `login` (flux /cli/auth + token 1 min : l'API n'a pas encore d'auth par jeton), `edit`, `add-registry`, `completion`, surface npm `/skills/@scope/name`, hooks Claude globaux (fusion dans settings.json), assistant plugin.

## 2026-10-13 — Phase 3, gouvernance (tranche 1 : utilisateurs, modération, audit)
- **Rôles en base** : table `users` ; `HttpDevAuthProvider` lit le rôle en base. L'utilisateur dev par défaut (env) est créé avec le rôle de `MYREGISTRY_DEV_USER_ROLE` à sa première visite ; les autres sont créés `Reader`. Un Admin gère rôles et activation (`/admin/users`). Un utilisateur désactivé perd tous ses droits.
- **Impersonation de test** : l'en-tête `X-Dev-User: email` simule un autre utilisateur, uniquement si `MYREGISTRY_AUTH_MODE` ≠ `oidc`. À retirer/ignorer en prod (désactivé automatiquement en mode oidc).
- **Dernier administrateur** : impossible de le rétrograder ou le désactiver.
- **Quarantaine** : désormais ≥ 5 signalements d'**utilisateurs distincts** en 1 h (corrige la limite notée à la tranche précédente).
- **Rejet** : `POST /api/catalog/{id}/reject {reason}` (motif obligatoire) → `Deprecated`, motif dans l'historique de statut.
- **Suppression de version** : la plus haute version SemVer (à défaut la plus récente) est protégée ; la seule version restante ne peut pas être supprimée (409). Modérateur+. `DeletionPolicy` testée unitairement.
- **Pages** : `/admin` (KPI, répartition par statut et famille, activité récente), `/admin/moderation` (filtres Pending/Approved/Deprecated/ToReview/Quarantaine + recherche), `/admin/reports`, `/admin/users`, `/admin/audit`.
- **Reste phase 3** : contrat de gouvernance (propriétaire, risque, classe de données, revue), page de configuration générale, Sanity Check/Réparer (dépend des feeds Git, phase 5), déquarantaine auto à revoir.

## 2026-10-13 — Phase 3, gouvernance (tranche 2 : contrat et configuration)
- **Contrat de gouvernance par famille d'artefact** (table `governance`) : propriétaire, niveau de risque (Low/Medium/High/Critical), classe de données (Public/Internal/Confidential/Restricted), fréquence de revue, capabilities. Les enums de docs/02 « à définir » sont fixés ainsi.
- **Édition** : auteur (créateur de la première version), SecurityValidator ou Admin ; un Moderator ne peut pas. Sans contrat, le propriétaire par défaut affiché est l'auteur (`defined=false`).
- **Revue due** : fréquence définie ET dernière revue (à défaut, dernière mise à jour du contrat) plus ancienne que la fréquence. « Marquer comme revu » fixe la date. Compteur « Revues dues » au tableau de bord. Pas encore de notification (phase 8).
- **Capabilities** : normalisées (minuscules, tirets), dédoublonnées, 20 max ; inventaire `GET /api/catalog/capabilities-inventory` (union avec nombre d'artefacts).
- **Configuration** (`/admin/config`, `GET|PUT /api/admin/settings`, Admin) : titre, namespace par défaut, URL publique, validés et audités. Le titre du bandeau et le namespace pré-rempli à la publication viennent des réglages (`SettingsCache`, 30 s, invalidé à l'enregistrement). `registry-info` expose `publicUrl`.
- **`SettingsCache` avec son propre scope** : évite les opérations concurrentes sur le DbContext du circuit Blazor.
- **Reste phase 3** : Sanity Check / Réparer (dépend des feeds Git, phase 5) ; déquarantaine automatique à revoir ; notification des revues dues (phase 8).
