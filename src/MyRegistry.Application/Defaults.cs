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
    /// <summary>Courriel de contact du registre (affiché dans le marketplace, facultatif).</summary>
    public const string ContactEmail = "contact_email";
    /// <summary>Posé par l'assistant de premier lancement ; réglage technique, non modifiable par l'utilisateur.</summary>
    public const string SetupCompleted = "setup_completed";
    /// <summary>Nombre de versions les plus récentes conservées par artefact ; 0 = rétention désactivée (défaut).</summary>
    public const string RetentionKeepVersions = "retention_keep_versions";
    /// <summary>Âge minimal (jours) d'une version dépréciée pour être supprimable ; 7 au moins.</summary>
    public const string RetentionMinAgeDays = "retention_min_age_days";
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
