using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;
using MyRegistry.Application;

namespace MyRegistry.Web.Services;

/// <summary>Convertit les noms de secrets (tirets) en clés de configuration (OIDC_CLIENT_SECRET) ; les secrets au nom inexploitable sont ignorés.</summary>
public sealed class KeyVaultKeyMapper : KeyVaultSecretManager
{
    public override bool Load(SecretProperties secret) => (secret.Enabled ?? true) && KeyVaultNames.ToConfigKey(secret.Name) is not null;
    public override string GetKey(KeyVaultSecret secret) => KeyVaultNames.ToConfigKey(secret.Name)!;
}
