variable "environment_name" {
  description = "Nom court de l'environnement : dev ou prod."
  type        = string
  validation {
    condition     = contains(["dev", "prod"], var.environment_name)
    error_message = "environment_name doit valoir dev ou prod."
  }
}

variable "location" {
  description = "Région Azure."
  type        = string
  default     = "francecentral"
}

variable "resource_group_name" {
  description = "Groupe de ressources de l'environnement (créé par infra/bootstrap.sh)."
  type        = string
}

variable "acr_name" {
  description = "Registre ACR partagé (créé une fois par bootstrap)."
  type        = string
}

variable "acr_resource_group" {
  description = "Groupe de ressources du registre ACR partagé."
  type        = string
}

variable "image_tag" {
  description = "Tag exact de l'image déployée (SHA de commit). Prod : épinglé dans envs/prod.tfvars, modifié uniquement par PR de promotion."
  type        = string
}

variable "public_url" {
  description = "URL publique de l'application."
  type        = string
}

variable "auth_mode" {
  description = "Mode d'authentification de l'application : dev ou oidc."
  type        = string
  default     = "oidc"
  validation {
    condition     = contains(["dev", "oidc"], var.auth_mode)
    error_message = "auth_mode doit valoir dev ou oidc."
  }
}

variable "db_admin_login" {
  description = "Administrateur du serveur PostgreSQL."
  type        = string
  default     = "myregistryadmin"
}

variable "db_admin_password" {
  description = "Mot de passe administrateur PostgreSQL. Fourni par TF_VAR_db_admin_password (secret d'environnement GitHub), jamais dans un fichier."
  type        = string
  sensitive   = true
}

variable "postgres_sku" {
  description = "SKU PostgreSQL Flexible Server."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "min_replicas" {
  type    = number
  default = 1
}

variable "max_replicas" {
  type    = number
  default = 3
}

variable "keyvault_purge_protection" {
  description = "Protection contre la purge du Key Vault (recommandée en prod)."
  type        = bool
  default     = false
}
