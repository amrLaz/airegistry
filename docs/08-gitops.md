# 08 — Livraison GitOps (Azure Container Apps, Terraform)

## Principe
**Git est la source de vérité de ce qui tourne.** L'infrastructure et la version déployée sont décrites par des fichiers versionnés ; on ne modifie rien à la main dans le portail Azure. Tout changement passe par une PR, est relu, puis appliqué par un pipeline. Un job de nuit détecte les écarts entre Git et la réalité.

| Élément | Où c'est décrit |
|---|---|
| Ressources Azure (ACA, PostgreSQL, Key Vault, Blob, identité, logs) | `infra/terraform/main.tf` |
| État d'un environnement (tag d'image, URL, tailles, authentification) | `infra/terraform/envs/dev.tfvars`, `prod.tfvars` |
| État Terraform (un par environnement) | compte de stockage Azure, `envs/<env>.backend.hcl` (jamais dans le dépôt) |
| Versions des fournisseurs | `infra/terraform/.terraform.lock.hcl` (commité) |
| Pipelines | `.github/workflows/` |
| Revue obligatoire sur l'infra | `.github/CODEOWNERS` |

## Flux
```
feat/xxx ──PR──► develop ──(CI verte)──► image :<sha> ──► terraform apply DEV
                                                  │
                         workflow "Promote" (SHA) ▼
                  PR "chore(deploy): promote" ──► main ──(approbation)──► terraform apply PROD
```
- **Dev** : chaque fusion dans `develop` construit l'image (`az acr build`), la tague avec le SHA du commit, puis `terraform plan` + `apply -var image_tag=<sha>`.
- **Prod** : le tag est **épinglé dans `infra/terraform/envs/prod.tfvars`** (`image_tag`). On promeut en lançant le workflow *Promote to prod* avec le SHA : il ouvre une PR vers `main`. La fusion déclenche le déploiement, soumis à l'approbation de l'environnement GitHub `prod`. Seule une image déjà testée en dev peut être promue ; le workflow vérifie qu'elle existe dans l'ACR avant d'appliquer.
- **Rollback** : `git revert` du commit de promotion (nouvelle PR), ou bascule du trafic vers la révision précédente d'Azure Container Apps (mode `Multiple`). Git garde la trace de ce qui a tourné quand.
- **PR d'infra** : le workflow *Infra preview* exécute `terraform plan` (sans verrou, lecture seule) et l'affiche dans le résumé de la PR.
- **Dérive** : le workflow *Drift detection* (quotidien) lance `terraform plan -detailed-exitcode` ; un écart (code 2) fait échouer le job. En dev, le tag comparé est celui de l'application réellement déployée.
- **CI** : `terraform fmt -check`, `init -backend=false` et `validate` sur chaque PR.

## Ce que Terraform crée par environnement
Log Analytics, identité managée applicative, PostgreSQL Flexible Server + base, Key Vault (RBAC) avec les secrets `db-connection` et `oidc-client-secret`, compte de stockage Blob (sans clé d'accès, conteneur privé), environnement et application Azure Container Apps (révisions multiples, sondes `/health` et `/health/ready`, **affinité de session** exigée par Blazor Server), et les attributions de rôles : `AcrPull` (registre partagé), `Key Vault Secrets User` et `Storage Blob Data Contributor` pour l'application.

L'application reçoit en variables d'environnement l'URI du coffre (`MYREGISTRY_KEYVAULT_URI`), l'URL Blob, la configuration Entra ID non secrète et `AZURE_CLIENT_ID` (sélection de l'identité managée). Les secrets sont lus **dans le coffre** par l'application elle-même (voir `DECISIONS.md`).

## Secrets
- Aucun secret Azure dans GitHub : connexion par **OIDC** (identifiants fédérés par environnement), pour `azure/login` comme pour le fournisseur Terraform (`ARM_USE_OIDC`) et l'état distant (`use_oidc`, `use_azuread_auth`).
- Deux secrets d'environnement GitHub, passés à Terraform par `TF_VAR_*` : `MYREGISTRY_DB_ADMIN_PASSWORD` et `MYREGISTRY_OIDC_CLIENT_SECRET`. Ils sont écrits dans le Key Vault par Terraform, puis lus par l'application via son identité managée.
- **L'état Terraform contient ces valeurs** (c'est inhérent à Terraform) : le compte d'état n'a pas de clé d'accès, est versionné, et seul le pipeline y accède par Entra ID. Ne donnez pas de droit de lecture sur ce compte à d'autres identités.
- Rien de sensible dans les fichiers `.tfvars`.

## Mise en route (une seule fois)
1. Relire puis exécuter `infra/bootstrap.sh` (ACR, groupes de ressources, compte d'état Terraform, identité OIDC du pipeline et ses droits).
2. Créer l'inscription d'application Entra ID de l'application (type Web, URI de redirection `https://<url>/signin-oidc`, voir le README) et noter tenant, client id, secret.
3. Renseigner dans GitHub, par environnement `dev` et `prod`, les variables et secrets listés en fin de script. Activer *Required reviewers* sur `prod`.
4. Renseigner dans `infra/terraform/envs/*.tfvars` : `public_url`, `storage_account_name` (unique au monde), `oidc_tenant_id`, `oidc_client_id`, `bootstrap_admins`.
5. Protéger `develop` et `main` : PR obligatoire, CI verte obligatoire, pas de push direct.
6. Premier déploiement : fusionner dans `develop`. L'image `bootstrap` du premier `apply` n'existe pas : le workflow de dev applique avec le SHA qu'il vient de construire.

## Travailler en local
```bash
cd infra/terraform
terraform init -backend=false     # valider sans toucher à l'état
terraform validate && terraform fmt -check -recursive
# Plan réel (nécessite az login et l'accès à l'état) :
export TF_VAR_db_admin_password=… TF_VAR_oidc_client_secret=…
terraform init -backend-config=envs/dev.backend.hcl
terraform plan -var-file=envs/dev.tfvars -var image_tag=<sha>
```

## Limites connues
- Le modèle est un « push piloté par Git » (GitHub Actions), pas un contrôleur en pull type Flux/Argo : Azure Container Apps n'en héberge pas. La dérive est détectée la nuit, pas corrigée en continu.
- Les migrations EF Core s'appliquent au démarrage de l'application ; avec plusieurs réplicas il faudra un verrou (advisory lock, voir `DECISIONS.md`).
- Pare-feu PostgreSQL ouvert aux services Azure : à remplacer par un VNet privé pour une prod durcie.
- L'affinité de session est posée par le fournisseur `azapi` (le fournisseur `azurerm` ne la gère pas) et reposée à chaque modification de l'application. Une dérive manuelle de ce seul réglage n'est pas détectée par `plan`.
- **Validé** : `terraform fmt`, `init -backend=false` et `validate`, la syntaxe YAML des workflows et la syntaxe de `bootstrap.sh`. **Jamais exécuté contre un abonnement Azure** : ni `plan`, ni `apply`, ni le script de bootstrap. Le premier `plan` réel pourra révéler des ajustements (noms globaux, quotas, propagation des rôles).
