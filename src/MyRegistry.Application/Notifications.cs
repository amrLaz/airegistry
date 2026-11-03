using MyRegistry.Domain;

namespace MyRegistry.Application;

public static class NotificationRules
{
    /// <summary>Tout est activé par défaut ; seule une préférence explicite à « faux » désactive un type.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<NotificationKind, bool> prefs, NotificationKind kind) =>
        !prefs.TryGetValue(kind, out var enabled) || enabled;

    /// <summary>Destinataires uniques (casse ignorée), sans valeur vide et sans l'auteur de l'action : on ne se notifie pas soi-même.</summary>
    public static IReadOnlyList<string> Recipients(IEnumerable<string?> candidates, string actor) =>
        candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim())
            .Where(c => !string.Equals(c, actor, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string Subject(string ns, string name, string version) => $"{ns}/{name}@{version}";
    public static string ArtifactLink(Guid versionId) => $"/artifact/{versionId}";
}
