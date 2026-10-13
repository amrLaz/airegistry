# Dossier de démarrage MyRegistry

## Contenu
- `CLAUDE.md` (à la racine du repo) : lu automatiquement par Claude Code.
- `docs/01..07` : spécification, architecture, API, CLI, règles, backlog, prompts.
- `docker-compose.yml`, `.env.example` : environnement local.
- `docs/reference/` : à remplir avec tes sources d'origine (voir ci-dessous).

## Mise en route
1. Crée le repo, copie ce dossier à la racine.
2. Dans `docs/reference/`, dépose : la documentation d'origine (texte), `openapi.json`, les captures d'écran de l'ancienne UI. Ce sont des références visuelles et fonctionnelles, pas du code à copier.
3. Ne laisse pas d'identifiants réels (tenant ID, client ID, emails) dans les captures versionnées. Floute-les ou garde `docs/reference/` hors git (`.gitignore`).
4. Lance `claude` dans le repo, puis colle le prompt de la phase 0 (docs/07-prompts.md).

## Ordre de lecture pour toi
01 spec → 02 architecture → 06 backlog → 07 prompts.
