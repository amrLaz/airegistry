# Backend d'état Terraform de l'environnement prod (compte créé par infra/bootstrap.sh).
resource_group_name  = "rg-myregistry-shared"
storage_account_name = "stmyregistrytfstate"
container_name       = "tfstate"
key                  = "prod.tfstate"
use_oidc             = true
use_azuread_auth     = true # le compte d'état n'a pas de clé d'accès
