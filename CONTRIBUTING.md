# Conventions de contribution

## Auteur
Tous les commits et PR sont **signés `amr.lazraq`**.
```bash
git config user.name "amr.lazraq"
```

## Nommage des branches
`<type>/<description-kebab-case>`

| Type | Usage |
|---|---|
| `feat/` | nouvelle fonctionnalité |
| `fix/` | correction de bug |
| `docs/` | documentation |
| `test/` | ajout ou correction de tests |
| `refactor/` | refonte sans changement de comportement |
| `chore/` | outillage, dépendances |
| `ci/` | pipelines |

Exemples : `feat/catalogue-recherche`, `fix/sha256-download`.
Le caractère `:` est interdit par Git dans un nom de branche : on utilise `/`.

## Commits
Format conventionnel : `type(scope): résumé à l'impératif`, ex. `feat(api): ajouter POST /api/validate`.
Une tranche verticale (API + UI + tests + doc) = un commit.

## Pull requests
Titre au même format que les commits, description mentionnant l'auteur `amr.lazraq`, tests verts (`dotnet test`, `cd cli && npm test`).

## Pour Claude Code
Le skill `git-conventions` (`.claude/skills/git-conventions/`) applique ces règles.
