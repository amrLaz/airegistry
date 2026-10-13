#!/usr/bin/env bash
# Initialisation UNIQUE du socle (hors GitOps : ce qui permet à GitOps de fonctionner).
# Prérequis : az login avec un compte Owner sur l'abonnement. Relire avant d'exécuter.
set -euo pipefail

: "${LOCATION:=francecentral}"
: "${ACR_NAME:=myregistryacr}"            # doit correspondre à acrName dans infra/params/*.bicepparam
: "${SHARED_RG:=rg-myregistry-shared}"
: "${GITHUB_REPO:=amrLaz/airegistry}"
SUB_ID=$(az account show --query id -o tsv)

az group create -n "$SHARED_RG" -l "$LOCATION" >/dev/null
az acr create -g "$SHARED_RG" -n "$ACR_NAME" --sku Basic >/dev/null
for env in dev prod; do az group create -n "rg-myregistry-$env" -l "$LOCATION" >/dev/null; done

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

cat <<EOT

À configurer dans GitHub (Settings > Environments), pour CHAQUE environnement dev et prod :
  Variables : AZURE_CLIENT_ID=$APP_ID
              AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)
              AZURE_SUBSCRIPTION_ID=$SUB_ID
              AZURE_RESOURCE_GROUP=rg-myregistry-<env>
  Secret    : MYREGISTRY_DB_ADMIN_PASSWORD (mot de passe fort, différent par environnement)
  prod      : activer "Required reviewers" (approbation avant déploiement) et limiter aux branches main.
EOT
