namespace MyRegistry.Application;

public static class SettingKeys
{
    public const string Title = "title";
    public const string DefaultNamespace = "default_namespace";
    public const string PublicUrl = "public_url";
    /// <summary>Dépôt Git recevant le snapshot de la marketplace (file:///chemin pour l'instant).</summary>
    public const string MarketplaceRepo = "marketplace_repo";
    /// <summary>Analyse de sécurité directe à la publication (« true » par défaut).</summary>
    public const string SecurityScanEnabled = "security_scan_enabled";
    public const string SnapshotDigest = "marketplace_snapshot_digest";
    public const string SnapshotAt = "marketplace_snapshot_at";
    public const string SnapshotCommit = "marketplace_snapshot_commit";
}

/// <summary>Valeurs initiales injectées en base au premier démarrage uniquement.</summary>
public static class SettingDefaults
{
    public static IReadOnlyDictionary<string, string> Initial { get; } = new Dictionary<string, string>
    {
        [SettingKeys.Title] = "MyRegistry",
        [SettingKeys.DefaultNamespace] = "myregistry",
        [SettingKeys.SecurityScanEnabled] = "true",
    };
}
