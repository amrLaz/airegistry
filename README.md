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
Commandes disponibles : `config`, `list [-r]`, `add`, `remove`, `outdated`, `update`, `pack`, `check`, `publish [--dry-run]`, `version`. Pas encore : `login`, `edit`, `add-registry`, `completion`.

## Avec PostgreSQL (plus tard)
```bash
docker compose up -d db
export MYREGISTRY_DB_CONNECTION="Host=localhost;Port=5432;Database=myregistry;Username=myregistry;Password=myregistry"
dotnet run --project src/MyRegistry.Web
```

## Tests
`dotnet test`. Les tests d'intégration utilisent PostgreSQL via `MYREGISTRY_TEST_DB` ou Testcontainers.
