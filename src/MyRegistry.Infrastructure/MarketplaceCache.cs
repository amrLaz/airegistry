using System.Collections.Concurrent;

namespace MyRegistry.Infrastructure;

/// <summary>Cache de /marketplace.json : 5 minutes, invalidé à chaque changement de visibilité (approbation, quarantaine, retrait...).</summary>
public sealed class MarketplaceCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (string Body, string ETag, DateTime At)> _items = new();

    public bool TryGet(string key, out (string Body, string ETag) value)
    {
        if (_items.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < Ttl) { value = (e.Body, e.ETag); return true; }
        value = default;
        return false;
    }

    public void Set(string key, string body, string etag) => _items[key] = (body, etag, DateTime.UtcNow);
    public void Invalidate() => _items.Clear();
}
