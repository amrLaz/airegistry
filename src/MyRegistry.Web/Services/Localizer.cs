namespace MyRegistry.Web.Services;

/// <summary>Traductions FR/EN minimales ; langue modifiable à l'exécution.</summary>
public sealed class Localizer
{
    private static readonly Dictionary<string, (string Fr, string En)> Texts = new()
    {
        ["welcome"] = ("Bienvenue sur", "Welcome to"),
        ["tagline"] = ("Le registre interne des artefacts IA : serveurs MCP, skills, agents, prompts, plugins, hooks.",
                       "The internal registry for AI artifacts: MCP servers, skills, agents, prompts, plugins, hooks."),
        ["signedin"] = ("Connecté en tant que", "Signed in as"),
        ["role"] = ("Rôle", "Role"),
        ["catalog"] = ("Catalogue", "Catalog"),
        ["soon"] = ("Disponible en phase 1", "Coming in phase 1"),
        ["theme"] = ("Thème", "Theme"),
        ["lang"] = ("Langue", "Language"),
    };

    public string Lang { get; private set; } = "fr";
    public event Action? Changed;

    public string this[string key] =>
        Texts.TryGetValue(key, out var t) ? (Lang == "fr" ? t.Fr : t.En) : key;

    public void Toggle()
    {
        Lang = Lang == "fr" ? "en" : "fr";
        Changed?.Invoke();
    }
}
