# MyRegistry — instructions pour Claude Code

Registre interne d'artefacts IA : serveurs MCP, skills, agents, prompts, plugins, hooks.
Catalogue, versioning SemVer, modération, CLI, marketplace Git, scan de sécurité.
Je développe seul avec toi. Travaille par tranches verticales (API + UI + tests + doc).

## Lis d'abord (dans l'ordre)
1. docs/01-product-spec.md : le quoi
2. docs/02-architecture.md : le comment (stack, modèle de données)
3. docs/03-api-contract.md : endpoints
4. docs/04-cli-spec.md : CLI
5. docs/05-validation-rules.md : règles métier
6. docs/06-backlog.md : phases et critères d'acceptation
7. docs/07-prompts.md : prompts par phase

## Règles de nommage (strictes)
- Le produit s'appelle **MyRegistry**. Aucun identifiant `up`, `upai`, `upcoop`, `fabrik` dans le code, la config, les tests ou la doc.
- Projet .NET : `MyRegistry.*`. CLI : `myregistry` (bin). Config projet : `.myregistry.json`. Config globale : `~/.myregistry/config.json`. Lockfile : `myregistry.lock`. Marqueur run : `.myregistry/run.json`.
- Variables d'env : `MYREGISTRY_*`. Namespace par défaut : `myregistry` (configurable en base).
- Titre, namespace par défaut, URL publique : en base/config, jamais en dur.

## Stack
- Backend : ASP.NET Core (dernier LTS), EF Core, PostgreSQL, OpenAPI (Scalar sur /scalar/v1).
- UI : Blazor Server + MudBlazor, responsive (utilisation mobile fréquente), thème clair/sombre, FR/EN.
- CLI : Node 20+, TypeScript, publiée via npm (`bin: myregistry`).
- Conteneurs : Docker, `docker compose up` lance tout en local. Déploiement cible : Azure Container Apps (plus tard).

## Git (obligatoire)
Respecte `CONTRIBUTING.md` via le skill `git-conventions` : branches `feat/`, `fix/`, `docs/`..., commits conventionnels, auteur toujours `amr.lazraq`.

## Livraison (GitOps)
Git est la source de vérité : infra dans `infra/` (Bicep), versions déployées dans `infra/params/*.bicepparam`, pipelines dans `.github/workflows/`. Aucune modification manuelle dans Azure ; tout passe par PR. Voir `docs/08-gitops.md`.

## Conventions de code
- Architecture en couches : Domain / Application / Infrastructure / Web. Pas de logique métier dans les contrôleurs ni les composants Blazor.
- Un `IGitProvider` (Azure DevOps lecture/écriture, GitHub lecture seule). GitLab hors périmètre.
- Les jobs de fond passent par un `IBackgroundJob` + verrou PostgreSQL (advisory lock) : ils doivent être sûrs en multi-réplicas.
- Aucun fichier métier sur le disque du conteneur : contenu en base (ou `IBlobStore`, implémentation locale puis Azure Blob). Le cache disque est jetable.
- Secrets jamais dans le code ni l'image : variables d'env, puis Key Vault.
- Auth : `IAuthProvider` ; en dev, utilisateur factice avec rôle choisi ; en prod Entra ID (OIDC).
- Tests : xUnit + Testcontainers (PostgreSQL réel). Écris les tests des règles de docs/05 AVANT le code.
- Chaque endpoint a son entrée OpenAPI ; le client CLI est généré depuis l'OpenAPI.

## Commandes
- `docker compose up -d db` puis `dotnet run --project src/MyRegistry.Web`
- `docker compose up --build` : stack complète
- `dotnet test`
- `cd cli && npm test`

## Méthode de travail
1. Annonce un plan court avant de coder.
2. Une tranche = un commit (message conventionnel : feat/fix/test/docs).
3. Ne sors pas du périmètre de la phase en cours (voir docs/06-backlog.md).
4. En cas d'ambiguïté, applique l'hypothèse la plus simple, note-la dans docs/DECISIONS.md et continue.
5. Termine chaque tranche par : tests verts, OpenAPI à jour, doc mise à jour.

## Hors périmètre initial
GitLab, notifications Slack/Teams, garde-fous de poste, cockpit d'orchestration, serveur MCP, plugins Cursor. Voir phase 8.
