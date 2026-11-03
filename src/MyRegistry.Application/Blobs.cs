using System.Text.RegularExpressions;

namespace MyRegistry.Application;

/// <summary>Stockage d'objets (local en développement, Azure Blob en production). Aucun fichier métier ne doit rester sur le disque du conteneur.</summary>
public interface IBlobStore
{
    Task PutAsync(string key, byte[] data, string? contentType = null, CancellationToken ct = default);
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}

public static class BlobKeys
{
    private static readonly Regex Segment = new("^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    /// <summary>Clé = segments séparés par « / », caractères sûrs uniquement ; ni « . » ni « .. », ni chemin absolu, ni 512 caractères dépassés.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 512) return false;
        return key.Split('/').All(s => s is not ("." or "..") && Segment.IsMatch(s));
    }

    public static string Require(string? key) => IsValid(key) ? key! : throw new ArgumentException($"Clé de blob invalide : « {key} ».", nameof(key));

    private static readonly Regex Container = new("^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$", RegexOptions.Compiled);

    /// <summary>Nom de conteneur Azure : 3 à 63 caractères, minuscules, chiffres et tirets isolés.</summary>
    public static bool IsValidContainer(string? name) => name is not null && Container.IsMatch(name);

    public static bool IsValidPrefix(string? prefix) => prefix is null or "" || IsValid(prefix.TrimEnd('/'));
}

public static class KeyVaultNames
{
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9-]{0,126}$", RegexOptions.Compiled);

    /// <summary>Un secret Key Vault « oidc-client-secret » devient la clé de configuration « OIDC_CLIENT_SECRET » (les noms de secrets n'acceptent pas le souligné). Null si le nom est invalide.</summary>
    public static string? ToConfigKey(string secretName) => Name.IsMatch(secretName) ? secretName.Replace('-', '_').ToUpperInvariant() : null;
}
