environment_name    = "prod"
resource_group_name = "rg-myregistry-prod"
acr_name            = "myregistryacr"
acr_resource_group  = "rg-myregistry-shared"
# ÉTAT DÉSIRÉ DE PROD : tag épinglé ici. Le changer = une PR de promotion vers main, jamais un déploiement manuel.
image_tag                 = "bootstrap"
public_url                = "https://myregistry.example.com"
auth_mode                 = "oidc"
min_replicas              = 2
max_replicas              = 6
postgres_sku              = "GP_Standard_D2ds_v5"
keyvault_purge_protection = true

# Stockage d'objets : nom unique au monde, à adapter si déjà pris.
storage_account_name = "stmyregistryprod"

# Authentification Entra ID (auth_mode = oidc). Le secret client vient de TF_VAR_oidc_client_secret.
oidc_tenant_id       = "00000000-0000-0000-0000-000000000000" # à remplacer
oidc_client_id       = "00000000-0000-0000-0000-000000000000" # à remplacer
bootstrap_admins     = ""                                     # ex. "prenom.nom@exemple.com"
oidc_allowed_domains = ""                                     # ex. "exemple.com" ; vide = tous
