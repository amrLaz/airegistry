using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MyRegistry.Application;

namespace MyRegistry.Infrastructure;

/// <summary>Stockage sur disque pour le développement et les tests. La clé est validée puis le chemin complet est contrôlé (pas d'évasion du dossier racine).</summary>
public sealed class LocalBlobStore : IBlobStore
{
    private readonly string _root;
    public LocalBlobStore(string root) { _root = Path.GetFullPath(root); Directory.CreateDirectory(_root); }

    private string PathOf(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, BlobKeys.Require(key).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Clé hors du dossier racine.", nameof(key));
        return full;
    }

    public async Task PutAsync(string key, byte[] data, string? contentType = null, CancellationToken ct = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct);
        File.Move(tmp, path, overwrite: true); // écriture atomique : un lecteur ne voit jamais un fichier à moitié écrit
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => File.Exists(PathOf(key)) ? await File.ReadAllBytesAsync(PathOf(key), ct) : null;
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathOf(key)));
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        var p = PathOf(key);
        if (!File.Exists(p)) return Task.FromResult(false);
        File.Delete(p);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        if (!BlobKeys.IsValidPrefix(prefix)) throw new ArgumentException("Préfixe invalide.", nameof(prefix));
        var all = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(k => k.StartsWith(prefix ?? "", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return Task.FromResult<IReadOnlyList<string>>(all);
    }
}

/// <summary>Azure Blob Storage. En production l'accès passe par l'identité managée (rôle « Storage Blob Data Contributor »), pas par une clé.</summary>
public sealed class AzureBlobStore(BlobContainerClient container) : IBlobStore
{
    private bool _ensured;

    private async Task<BlobClient> ClientAsync(string key, CancellationToken ct)
    {
        BlobKeys.Require(key);
        if (!_ensured) { await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct); _ensured = true; }
        return container.GetBlobClient(key);
    }

    public async Task PutAsync(string key, byte[] data, string? contentType = null, CancellationToken ct = default)
    {
        var blob = await ClientAsync(key, ct);
        await blob.UploadAsync(new BinaryData(data), new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType ?? "application/octet-stream" } }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var blob = await ClientAsync(key, ct);
        try { return (await blob.DownloadContentAsync(ct)).Value.Content.ToArray(); }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) => (await (await ClientAsync(key, ct)).ExistsAsync(ct)).Value;
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default) => (await (await ClientAsync(key, ct)).DeleteIfExistsAsync(cancellationToken: ct)).Value;

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        if (!BlobKeys.IsValidPrefix(prefix)) throw new ArgumentException("Préfixe invalide.", nameof(prefix));
        await ClientAsync("x", ct);
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct)) names.Add(item.Name);
        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}

public static class BlobStoreFactory
{
    /// <summary>Azure Blob par chaîne de connexion (émulateur, développement), par URL de compte + identité managée (production), sinon dossier local jetable.</summary>
    public static IBlobStore Create(string? connectionString, string? accountUrl, string? container, string? localPath)
    {
        container = string.IsNullOrWhiteSpace(container) ? "myregistry" : container.Trim();
        if (!BlobKeys.IsValidContainer(container)) throw new InvalidOperationException($"Nom de conteneur Blob invalide : « {container} » (3 à 63 caractères, minuscules, chiffres, tirets).");
        if (!string.IsNullOrWhiteSpace(connectionString)) return new AzureBlobStore(new BlobContainerClient(connectionString, container));
        if (!string.IsNullOrWhiteSpace(accountUrl))
        {
            if (!Uri.TryCreate(accountUrl.TrimEnd('/') + "/" + container, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("MYREGISTRY_BLOB_ACCOUNT_URL doit être une URL https (ex. https://moncompte.blob.core.windows.net).");
            return new AzureBlobStore(new BlobContainerClient(uri, new Azure.Identity.DefaultAzureCredential()));
        }
        return new LocalBlobStore(string.IsNullOrWhiteSpace(localPath) ? "local-blobs" : localPath);
    }
}
