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

## 2026-10-13 — Phase 4, plugins et marketplace (tranche 1)
- **Composition** : `x-myregistry-components` = `[{type, slug, version}]`. Version SemVer **exacte** (aucune plage `^`/`~`), pas de plugin imbriqué, pas de doublon, pas d'auto-référence, 1 à 50 composants. À la publication interne, chaque composant doit exister **au type annoncé**, approuvé et hors quarantaine (sinon `component-unresolved` / `component-not-approved`). L'import externe (quarantaine au lieu de refus) viendra avec les feeds (phase 5).
- **Brouillons** (`plugin_drafts`) : privés à leur auteur (Admin voit tout), nom/composition tolérés incomplets tant qu'on n'a pas publié. Publier = `PublishService` avec un manifeste, puis lignes `plugin_components` ; version : SemVer proposée, sinon incrément automatique des règles de docs/05.
- **Archive du plugin** (`GET /api/plugins/{ns}/{name}/download?version`) : assemblée à la demande depuis les versions épinglées, pas stockée. Disposition : `skills/{nom}/…`, `agents/{nom}.md`, `prompts/{nom}.prompt.md`, `hooks/hooks.json` (événements fusionnés), `.mcp.json` (serveurs fusionnés), manifeste dans `.claude-plugin/`, `.github/plugin/` et `.codex-plugin/`. ZIP **déterministe** (dates fixées, chemins triés) ; `X-Artifact-Sha256` = SHA-256 de l'archive. Si un composant épinglé a été supprimé, l'archive n'est plus servie (404).
- **`/marketplace.json`** : dernière version approuvée non quarantinée de chaque plugin ; source `{"source":"archive","url":"<URL publique>/api/plugins/…"}` ; nom du plugin = `namespace-nom` (≤ 64, cf. docs/05). Cache 5 min **invalidé** à toute approbation/rejet/quarantaine/retrait/suppression/changement de réglages ; `ETag` + `If-None-Match` (304) + `Cache-Control: max-age=300`. L'URL publique vient des réglages, à défaut de la requête.
- **Badge « mise à jour disponible »** : une version de composant approuvée plus récente (SemVer) que celle épinglée, dans l'éditeur et sur la fiche d'un plugin publié.
- **Export/réimport** : ZIP contenant uniquement `draft.json` (1 Mo max, tout autre contenu refusé) ; l'import crée un nouveau brouillon appartenant à l'importateur.
- **Pas de `update`/`remove`/`outdated` pour les plugins côté CLI** (déjà refusé en phase 2).
- **Reste phase 4** : snapshot Git (`.github/plugin/marketplace.json` + `.claude-plugin`, `.agents/plugins`, `.cursor-plugin`) vers un dépôt local `file:///` ; CLI `add-registry --apply` ; assistant `add` pour plugins.

## 2026-10-13 — Phase 4, plugins et marketplace (tranche 2 : snapshot Git, add-registry)
- **`IGitProvider`** introduit (Application) avec une seule opération pour l'instant, `CommitFilesAsync`. `LocalGitProvider` utilise le binaire `git` (arguments en liste, pas de shell), un verrou global, auteur `MyRegistry <myregistry@localhost>`. `git` est installé dans l'image Docker. Azure DevOps (RW) et GitHub (RO) en phase 5.
- **Snapshot** : même manifeste écrit dans `.github/plugin/`, `.claude-plugin/`, `.agents/plugins/` et `.cursor-plugin/marketplace.json` ; un commit seulement si le digest (SHA-256 du manifeste) change. Déclenché manuellement (`POST /api/admin/marketplace/snapshot`, bouton sur `/admin/config`). **La publication automatique toutes les 6 h** dépend des jobs de fond avec advisory lock : phase 5.
- **Réglages** : `marketplace_repo` (chemin absolu ou `file:///…` ; distant, relatif, racine et `..` refusés) ; digest/date/commit du dernier snapshot stockés en réglages internes, non modifiables par l'utilisateur. Le snapshot exige l'URL publique (sinon les URL d'archive pointeraient vers localhost).
- **CLI `add-registry [--apply] [--marketplace URL] [-g]`** : sans `--apply`, décrit sans rien écrire. Avec `--apply`, fusionne `chat.plugins.marketplaces` (VS Code) et `extraKnownMarketplaces` (Claude Code), de façon idempotente ; refuse d'écraser un `settings.json` non JSON strict (commentaires). Codex : instructions seulement.
- **À VÉRIFIER avant usage réel** (non testé contre les vrais outils) : les noms de réglages (`chat.plugins.marketplaces`, `extraKnownMarketplaces`) et surtout le type de source `{"source":"archive","url":…}` de `marketplace.json`, retenu parce que docs/03 le demande ; si VS Code / Claude Code ne le supportent pas, il faudra servir les plugins comme dépôts Git (source `git`) ou adapter la forme du manifeste. Critère d'acceptation du backlog (« installable depuis VS Code via le repo local ») donc **non validé de bout en bout**.

## 2026-10-13 — Phase 5, feeds (tranche 1 : modèle, découverte, synchronisation)
- **Types de feed** : `AzureDevOps`, `GitHub` et `Local` (dépôt `file:///` : pour développer et tester sans réseau). Seul `Local` a un fournisseur pour l'instant ; GitHub/Azure DevOps sont **créables mais leur synchronisation échoue** avec « pas encore disponible » (et compte comme un échec, donc pause après 3). Leurs fournisseurs (REST) et la publication par PR sont la tranche suivante. `IGitProvider` gagne `ReadRepoAsync` ; `IGitProviderResolver` choisit le fournisseur par type.
- **`pat_ref`** = NOM d'une variable d'environnement `MYREGISTRY_*` contenant le jeton ; jamais le jeton. Un nom hors `MYREGISTRY_*` (ou un vrai jeton collé par erreur) est refusé.
- **Découverte** (`RepoDiscovery`) : plugin (manifeste runtime, claim de tout son sous-arbre), skill (`SKILL.md`, dossier entier), hook (`hooks.json`), MCP (`mcp.json`), agent (`*.agent.md`, `AGENTS.md`), prompt (`*.prompt.md`). Un fichier n'appartient qu'à un artefact. Ignorés : `.git/`, `node_modules/`, `.github/workflows/`. Fichiers > 1 Mo et au-delà de 3000 fichiers sont ignorés et signalés.
- **Delta** : la synchronisation est sautée si le commit n'a pas changé (`--force` pour forcer) ; sinon on relit l'arbre et on compare le hash de contenu par artefact (pas de diff Git fichier par fichier).
- **Statut des imports externes** : `ToReview` (jamais `Approved` automatiquement) tant qu'il n'y a pas de scan de sécurité (phase 6) ; origine `External`, créateur `feed:<nom>`. Un secret détecté met en **quarantaine** (au lieu du refus des publications internes) ; une erreur de structure fait **ignorer** l'artefact avec un message ; plugin dont les composants n'existent pas → quarantaine.
- **Noms** : namespace du feed = namespace du registre + `-gh` / `-azd` / `-local` (≤ 32), ou `namespaceOverride`. La règle « nom contenant son type » ne s'applique pas aux imports ; un slug déjà pris par une autre source est ignoré.
- **Version** : cascade `version` | `semver` | `release` du frontmatter, sinon `0.0.N` (N augmente seulement si le contenu change) ; collision → incrément de patch.
- **Absent du dépôt** → dernière version `Deprecated` (note `absent-from-repo`) ; s'il revient avec le même contenu, le statut précédent est restauré.
- **Pause automatique** après 3 échecs consécutifs (`Paused`, inactif) ; la réactivation manuelle remet le compteur à 0. **Suppression** d'un feed : cascade sur ses versions importées (et artefacts devenus vides, gouvernance, commentaires, signalements) ; le feed système (`isSystem`, un seul) est protégé.
- **Reste phase 5** : fournisseurs GitHub (lecture) et Azure DevOps (lecture/écriture), publication interne par PR (branche `artifact/{ns}/{name}/{version}-{id}`, squash à l'approbation, fermeture au rejet), jobs de fond (`IBackgroundJob` + advisory lock : sync par intervalle, snapshot toutes les 6 h), changement de namespace avec aperçu et alias 547 jours, Sanity Check.

## 2026-10-13 — Phase 5, jobs de fond et alias de namespace (tranche 2)
- **`IBackgroundJob` + `IJobLock`** : `PostgresAdvisoryLock` (`pg_try_advisory_lock` sur une connexion dédiée, libéré à la fermeture) ; `InProcessLock` en mode local SQLite. `JobExecutor` : verrou → relecture de la dernière exécution **stockée en base** (`job_last_<nom>`, partagée entre réplicas) → exécution → enregistrement. Deux réplicas qui se disputent un job l'exécutent **une seule fois** (test d'intégration) ; un job en échec n'est pas relancé en boucle serrée et libère son verrou.
- **Jobs** : `feed-sync` (passe chaque minute, synchronise les feeds actifs dont l'intervalle est échu) et `marketplace-snapshot` (toutes les 6 h ; sans effet si aucun dépôt n'est configuré ; le digest évite un commit inutile). Le « 5 min » des échéances de gouvernance reste à faire avec les notifications (phase 8).
- **Exécution** : `BackgroundJobRunner` (cycle de 30 s, démarrage différé de 10 s pour laisser passer les migrations), désactivé en test et avec `MYREGISTRY_JOBS=off` (utile pour un réplica qui ne sert que l'interface).
- **Changement de namespace d'un feed** : aperçu (`POST /namespace/preview`, n'écrit rien) puis application (`PUT /namespace`). Conflit (slug cible déjà pris par un artefact du même type) → 409 et rien ne change. Seuls les artefacts appartenant **exclusivement** au feed sont renommés. Les anciens slugs deviennent des **alias valables 547 jours** (`namespace_aliases`) résolus par `by-name`, `raw` et le téléchargement de plugin. Les synchronisations suivantes ajoutent des versions à l'artefact renommé (pas de doublon).
- **Limites connues** : l'alias n'est pas résolu par `GET /api/catalog/{id}` (l'id reste stable, donc inutile) ni dans les manifestes de marketplace déjà publiés (le snapshot suivant les corrige) ; les alias expirés ne sont pas purgés (ils sont seulement ignorés).
- **Reste phase 5** : fournisseurs GitHub (lecture) et Azure DevOps (lecture/écriture), publication interne par PR (branche `artifact/{ns}/{name}/{version}-{id}`, squash à l'approbation, fermeture au rejet, PR vide refusée), Sanity Check et « Réparer ».
