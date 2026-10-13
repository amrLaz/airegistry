terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  # État distant dans un compte de stockage Azure. Le conteneur, le compte et la clé sont fournis par
  # `-backend-config=envs/<env>.backend.hcl` : un état par environnement, jamais dans le dépôt.
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
  # Authentification OIDC en CI (ARM_USE_OIDC, ARM_CLIENT_ID, ARM_TENANT_ID, ARM_SUBSCRIPTION_ID) ; `az login` en local.
}
