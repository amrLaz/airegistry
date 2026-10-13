# MyRegistry

Registre interne d'artefacts IA (serveurs MCP, skills, agents, prompts, plugins, hooks). Voir `CLAUDE.md` et `docs/`.

## Lancer en local (sans PostgreSQL)
Prérequis : SDK .NET 10.
```bash
dotnet run --project src/MyRegistry.Web
```
Sans `MYREGISTRY_DB_CONNECTION`, l'application utilise un fichier SQLite jetable (`myregistry-local.db`, ignoré par git).

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

## Avec PostgreSQL (plus tard)
```bash
docker compose up -d db
export MYREGISTRY_DB_CONNECTION="Host=localhost;Port=5432;Database=myregistry;Username=myregistry;Password=myregistry"
dotnet run --project src/MyRegistry.Web
```

## Tests
`dotnet test`. Les tests d'intégration utilisent PostgreSQL via `MYREGISTRY_TEST_DB` ou Testcontainers.
