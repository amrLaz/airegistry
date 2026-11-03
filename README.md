# MyRegistry

Registre interne d'artefacts IA (serveurs MCP, skills, agents, prompts, plugins, hooks). Voir `CLAUDE.md` et `docs/`.

## Lancer en local (sans PostgreSQL)
Prérequis : SDK .NET 10.
```bash
dotnet run --project src/MyRegistry.Web
```
Sans `MYREGISTRY_DB_CONNECTION`, l'application utilise un fichier SQLite jetable (`src/MyRegistry.Web/myregistry-local.db`, ignoré par git ; supprime-le pour repartir de zéro).

| URL | Contenu |
|---|---|
| http://localhost:5000 (voir la console pour le port) | Accueil Blazor |
| `/health` | liveness |
| `/scalar/v1` | documentation interactive de l'API |

Rôle de l'utilisateur factice : `MYREGISTRY_DEV_USER_ROLE=Reader|Publisher|Moderator|SecurityValidator|Admin` (défaut `Admin`).

Publier un skill d'essai :
```bash
curl -X POST http://localhost:5000/api/import -H 'content-type: application/json' -d '{
  "type":"Skill","namespace":"myregistry","name":"pdf-extractor","fileName":"SKILL.md",
  "rawContent":"---\nname: pdf-extractor\ndescription: Extrait des PDF\n---\n# PDF"}'
```

## CLI `myregistry`
```bash
cd cli && npm install && npm run build
export MYREGISTRY_BASE_URL=http://localhost:5108      # URL affichée par « dotnet run »
node dist/index.js publish ./mon-skill --dry-run      # valider sans publier
node dist/index.js publish ./mon-skill                # soumettre (statut Pending)
# approuver l'artefact depuis l'interface (Admin), puis :
node dist/index.js add myregistry/mon-skill --agent claude
node dist/index.js list && node dist/index.js outdated
```
Commandes disponibles : `config`, `list [-r]`, `add`, `remove`, `outdated`, `update`, `pack`, `check`, `publish [--dry-run]`, `login`, `edit`, `add-registry`, `version`. Pas encore : `completion`.

## Authentification Entra ID (production)
Par défaut l'utilisateur est factice (Admin) : **ne l'exposez jamais sur un réseau partagé**. Pour activer Entra ID :
```bash
export MYREGISTRY_AUTH_MODE=oidc
export MYREGISTRY_OIDC_TENANT=<id-du-tenant>            # ou MYREGISTRY_OIDC_AUTHORITY=https://…/v2.0
export MYREGISTRY_OIDC_CLIENT_ID=<id-de-l-application>
export MYREGISTRY_OIDC_CLIENT_SECRET=<secret>            # jamais dans le dépôt : variable d'environnement, puis Key Vault
export MYREGISTRY_BOOTSTRAP_ADMINS=prenom.nom@exemple.com   # administrateurs au premier login (liste séparée par des virgules)
export MYREGISTRY_OIDC_ALLOWED_DOMAINS=exemple.com       # facultatif : restreint les domaines de messagerie
```
Dans l'inscription d'application Entra ID : type **Web**, URI de redirection `https://<votre-url>/signin-oidc`, URI de déconnexion `https://<votre-url>/signout-callback-oidc`. Les rôles restent gérés en base (page Utilisateurs) : le jeton ne donne jamais de droits. L'application refuse de démarrer si la configuration est incomplète.

## Serveur MCP du registre
Le registre est lui-même un serveur MCP (lecture seule) : un agent peut chercher, lire et installer des artefacts.
```bash
claude mcp add --transport http myregistry http://localhost:5108/mcp        # Claude Code
# VS Code (.vscode/mcp.json) : { "servers": { "myregistry": { "type": "http", "url": "http://localhost:5108/mcp" } } }
```
Outils : `search_artifacts`, `get_artifact`, `get_artifact_file`, `install_instructions`. Seules les versions approuvées et non quarantinées sont visibles ; le contenu renvoyé est précédé d'un avertissement « donnée non fiable » (injection de prompt).

## Secrets et stockage (production)
- **Key Vault** : `MYREGISTRY_KEYVAULT_URI=https://<coffre>.vault.azure.net/` (identité managée ou `az login` en local). Un secret `oidc-client-secret` devient `OIDC_CLIENT_SECRET`, `db-connection` devient `DB_CONNECTION`, etc. (les noms de secrets n'acceptent pas le souligné ; les tirets sont convertis). Le coffre est lu au démarrage et **prime** sur les variables d'environnement ; s'il est injoignable l'application ne démarre pas.
- **Stockage d'objets** : `MYREGISTRY_BLOB_ACCOUNT_URL=https://<compte>.blob.core.windows.net` (identité managée, rôle *Storage Blob Data Contributor*) ou `MYREGISTRY_BLOB_CONNECTION_STRING` (émulateur Azurite), conteneur `MYREGISTRY_BLOB_CONTAINER` (défaut `myregistry`). Sans configuration : dossier local `local-blobs/` (développement uniquement, ignoré par git).

## Avec PostgreSQL (plus tard)
```bash
docker compose up -d db
export MYREGISTRY_DB_CONNECTION="Host=localhost;Port=5432;Database=myregistry;Username=myregistry;Password=myregistry"
dotnet run --project src/MyRegistry.Web
```

## Tests
`dotnet test`. Les tests d'intégration utilisent PostgreSQL via `MYREGISTRY_TEST_DB` ou Testcontainers.
