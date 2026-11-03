#!/usr/bin/env bash
# Initialisation UNIQUE du socle (hors GitOps : ce qui permet à GitOps de fonctionner).
# Prérequis : az login avec un compte Owner sur l'abonnement. Relire avant d'exécuter.
set -euo pipefail

: "${LOCATION:=francecentral}"
: "${ACR_NAME:=myregistryacr}"            # doit correspondre à acr_name dans infra/terraform/envs/*.tfvars
: "${SHARED_RG:=rg-myregistry-shared}"
: "${GITHUB_REPO:=amrLaz/airegistry}"
: "${STATE_ACCOUNT:=stmyregistrytfstate}"   # doit correspondre à storage_account_name dans infra/terraform/envs/*.backend.hcl
: "${STATE_CONTAINER:=tfstate}"
SUB_ID=$(az account show --query id -o tsv)

az group create -n "$SHARED_RG" -l "$LOCATION" >/dev/null
az acr create -g "$SHARED_RG" -n "$ACR_NAME" --sku Basic >/dev/null
for env in dev prod; do az group create -n "rg-myregistry-$env" -l "$LOCATION" >/dev/null; done

# État Terraform : compte de stockage dédié, sans clé d'accès (Entra ID uniquement), versionné et protégé contre la suppression.
az storage account create -g "$SHARED_RG" -n "$STATE_ACCOUNT" -l "$LOCATION" --sku Standard_GRS --kind StorageV2 \
  --min-tls-version TLS1_2 --allow-blob-public-access false --allow-shared-key-access false >/dev/null
az storage account blob-service-properties update -g "$SHARED_RG" --account-name "$STATE_ACCOUNT" \
  --enable-versioning true --enable-delete-retention true --delete-retention-days 30 >/dev/null
STATE_ID=$(az storage account show -g "$SHARED_RG" -n "$STATE_ACCOUNT" --query id -o tsv)
# Votre propre compte doit pouvoir créer le conteneur : rôle de données, puis attente de propagation.
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" --role "Storage Blob Data Owner" --scope "$STATE_ID" >/dev/null
sleep 30
az storage container create --account-name "$STATE_ACCOUNT" -n "$STATE_CONTAINER" --auth-mode login >/dev/null

# Identité du pipeline (OIDC, sans secret) avec un identifiant fédéré par environnement GitHub.
APP_ID=$(az ad app create --display-name "myregistry-gitops" --query appId -o tsv)
az ad sp create --id "$APP_ID" >/dev/null
for env in dev prod; do
  az ad app federated-credential create --id "$APP_ID" --parameters "{
    \"name\": \"github-$env\", \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$GITHUB_REPO:environment:$env\", \"audiences\": [\"api://AzureADTokenExchange\"]}" >/dev/null
  az role assignment create --assignee "$APP_ID" --role Contributor --scope "/subscriptions/$SUB_ID/resourceGroups/rg-myregistry-$env" >/dev/null
  # Les déploiements attribuent des rôles (AcrPull, Key Vault) : droit de gestion des accès sur ces groupes.
  az role assignment create --assignee "$APP_ID" --role "Role Based Access Control Administrator" --scope "/subscriptions/$SUB_ID/resourceGroups/rg-myregistry-$env" >/dev/null
done
az role assignment create --assignee "$APP_ID" --role "Role Based Access Control Administrator" --scope "/subscriptions/$SUB_ID/resourceGroups/$SHARED_RG" >/dev/null
az role assignment create --assignee "$APP_ID" --role AcrPush --scope "$(az acr show -n "$ACR_NAME" --query id -o tsv)" >/dev/null
# Le pipeline lit et écrit l'état Terraform.
az role assignment create --assignee "$APP_ID" --role "Storage Blob Data Contributor" --scope "$STATE_ID" >/dev/null

cat <<EOT

À configurer dans GitHub (Settings > Environments), pour CHAQUE environnement dev et prod :
  Variables : AZURE_CLIENT_ID=$APP_ID
              AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)
              AZURE_SUBSCRIPTION_ID=$SUB_ID
              AZURE_RESOURCE_GROUP=rg-myregistry-<env>
  Secrets   : MYREGISTRY_DB_ADMIN_PASSWORD (mot de passe fort, différent par environnement)
              MYREGISTRY_OIDC_CLIENT_SECRET (secret client de l'inscription Entra ID de l'application)
  Fichiers  : infra/terraform/envs/<env>.tfvars (oidc_tenant_id, oidc_client_id, bootstrap_admins, public_url, storage_account_name)
  prod      : activer "Required reviewers" (approbation avant déploiement) et limiter aux branches main.
EOT
