# 08 — Livraison GitOps (Azure Container Apps)

## Principe
**Git est la source de vérité de ce qui tourne.** L'infrastructure et la version déployée sont décrites par des fichiers versionnés ; on ne modifie rien à la main dans le portail Azure. Tout changement passe par une PR, est relu, puis appliqué par un pipeline. Un job de nuit détecte les écarts entre Git et la réalité.

| Élément | Où c'est décrit |
|---|---|
| Ressources Azure (ACA, PostgreSQL, Key Vault, identité, logs) | `infra/main.bicep` |
| État d'un environnement (tag d'image, URL, tailles) | `infra/params/dev.bicepparam`, `prod.bicepparam` |
| Pipelines | `.github/workflows/` |
| Revue obligatoire sur l'infra | `.github/CODEOWNERS` |

## Flux
```
feat/xxx ──PR──► develop ──(CI verte)──► image :<sha> ──► déploiement auto DEV
                                                  │
                         workflow "Promote" (SHA) ▼
                  PR "chore(deploy): promote" ──► main ──(approbation)──► déploiement PROD
```
- **Dev** : chaque fusion dans `develop` construit l'image (`az acr build`), la tague avec le SHA du commit et déploie.
- **Prod** : le tag est **épinglé dans `infra/params/prod.bicepparam`**. On promeut en lançant le workflow *Promote to prod* avec le SHA : il ouvre une PR vers `main`. La fusion déclenche le déploiement, soumis à l'approbation de l'environnement GitHub `prod`. Seule une image déjà testée en dev peut être promue.
- **Rollback** : `git revert` du commit de promotion (nouvelle PR), ou bascule du trafic vers la révision précédente d'Azure Container Apps (mode `Multiple`). Git garde la trace de ce qui a tourné quand.
- **PR d'infra** : le workflow *Infra preview* exécute un `what-if` et l'affiche dans le résumé de la PR.
- **Dérive** : le workflow *Drift detection* (quotidien) échoue si l'état réel diffère de Git.

## Secrets
- Aucun secret Azure dans GitHub : connexion par **OIDC** (identifiants fédérés par environnement).
- Seul `MYREGISTRY_DB_ADMIN_PASSWORD` est un secret d'environnement GitHub, utilisé uniquement pour créer le serveur PostgreSQL. La chaîne de connexion est ensuite stockée dans **Key Vault** et lue par l'application via son identité managée (`secretRef`).
- Rien de sensible dans les fichiers de paramètres.

## Mise en route (une seule fois)
1. Relire puis exécuter `infra/bootstrap.sh` (crée l'ACR, les groupes de ressources, l'identité OIDC et ses droits).
2. Renseigner dans GitHub, par environnement `dev` et `prod`, les variables et le secret listés en fin de script. Activer *Required reviewers* sur `prod`.
3. Protéger `develop` et `main` : PR obligatoire, CI verte obligatoire, pas de push direct.
4. Remplacer `acrName`, `publicUrl` et les domaines dans `infra/params/*.bicepparam`.
5. Premier déploiement : fusionner dans `develop`.

## Limites connues
- Le modèle est un « push piloté par Git » (GitHub Actions), pas un contrôleur en pull type Flux/Argo : Azure Container Apps n'en héberge pas. La dérive est détectée la nuit, pas corrigée en continu.
- Les migrations EF Core s'appliquent au démarrage de l'application ; avec plusieurs réplicas il faudra un verrou (advisory lock, voir `DECISIONS.md`).
- Pare-feu PostgreSQL ouvert aux services Azure : à remplacer par un VNet privé pour une prod durcie.
- Le contenu des artefacts est en base (`MYREGISTRY_STORAGE=local` sans écriture disque métier) ; `IBlobStore` Azure Blob viendra en phase 7.
- Rien de tout cela n'a été exécuté contre un abonnement Azure : seuls la syntaxe Bicep et le YAML des workflows ont été validés.
