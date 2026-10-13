---
name: git-conventions
description: Conventions Git de MyRegistry. À utiliser avant tout création de branche, commit, push ou PR : nommage des branches (feat/, fix/, ...), messages de commit conventionnels, auteur amr.lazraq.
---

# Conventions Git — MyRegistry

Source de vérité : `CONTRIBUTING.md` à la racine. Applique-la à chaque opération Git.

## Avant de commencer
1. Vérifie l'auteur : `git config user.name` doit valoir `amr.lazraq`. Sinon :
   `git config user.name "amr.lazraq"` (et `user.email` = email de amr.lazraq).
2. Vérifie le nom de la branche courante (voir ci-dessous).

## Branches
Format : `<type>/<description-kebab-case>`, où `<type>` ∈ `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `ci`.
Exemples : `feat/catalogue-recherche`, `fix/sha256-download`, `docs/api-contract`.
Les `:` sont interdits dans un nom de branche Git : le séparateur est `/`.
Si une branche imposée par l'outil ne respecte pas le format, signale-le avant de pousser.

## Commits
- Message conventionnel : `type(scope): résumé impératif` (≤ 72 car.), ex. `feat(api): ajouter POST /api/validate`.
- Une tranche verticale = un commit.
- Auteur : toujours `amr.lazraq` (`git commit` utilise la config ; ne jamais surcharger avec `--author`).

## Pull requests
- Titre = même format que le commit : `feat(scope): ...`.
- Mentionne `amr.lazraq` comme auteur dans la description.
- Pas de PR sans demande explicite.

## Interdits
Pas de `push --force` sur une branche partagée, pas de commit direct sur `main`, aucun secret versionné.
