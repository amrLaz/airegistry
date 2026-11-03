# Backend d'état Terraform de l'environnement dev (compte créé par infra/bootstrap.sh).
resource_group_name  = "rg-myregistry-shared"
storage_account_name = "stmyregistrytfstate"
container_name       = "tfstate"
key                  = "dev.tfstate"
use_oidc             = true
