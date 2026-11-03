environment_name    = "dev"
resource_group_name = "rg-myregistry-dev"
acr_name            = "myregistryacr"
acr_resource_group  = "rg-myregistry-shared"
# Dev : le tag est injecté par le workflow deploy.yml (SHA du commit de develop) : -var image_tag=<sha>
image_tag    = "bootstrap"
public_url   = "https://dev.myregistry.example.com"
auth_mode    = "oidc"
min_replicas = 1
max_replicas = 2

# Stockage d'objets : nom unique au monde, à adapter si déjà pris.
storage_account_name = "stmyregistrydev"

# Authentification Entra ID (auth_mode = oidc). Le secret client vient de TF_VAR_oidc_client_secret.
oidc_tenant_id       = "00000000-0000-0000-0000-000000000000" # à remplacer
oidc_client_id       = "00000000-0000-0000-0000-000000000000" # à remplacer
bootstrap_admins     = ""                                     # ex. "prenom.nom@exemple.com"
oidc_allowed_domains = ""                                     # ex. "exemple.com" ; vide = tous
