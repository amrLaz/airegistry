namespace MyRegistry.Application;

public static class SettingKeys
{
    public const string Title = "title";
    public const string DefaultNamespace = "default_namespace";
    public const string PublicUrl = "public_url";
}

/// <summary>Valeurs initiales injectées en base au premier démarrage uniquement.</summary>
public static class SettingDefaults
{
    public static IReadOnlyDictionary<string, string> Initial { get; } = new Dictionary<string, string>
    {
        [SettingKeys.Title] = "MyRegistry",
        [SettingKeys.DefaultNamespace] = "myregistry",
    };
}
