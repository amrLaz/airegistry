using MyRegistry.Domain;

namespace MyRegistry.Application;

public static class SetupRules
{
    /// <summary>L'assistant ne se propose qu'à un Admin, sur une instance vierge (aucun artefact) dont l'assistant n'a pas été terminé.</summary>
    public static bool NeedsSetup(string? completedSetting, int artifactCount, Role role) =>
        role == Role.Admin && artifactCount == 0 && !string.Equals(completedSetting, "true", StringComparison.OrdinalIgnoreCase);

    public static string MarketplaceUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/marketplace.json";

    /// <summary>Commandes pour brancher ce registre à Claude Code et VS Code (à valider contre les vrais outils : voir docs/DECISIONS.md).</summary>
    public static (string Claude, string VsCode) MarketplaceSnippets(string baseUrl)
    {
        var url = MarketplaceUrl(baseUrl);
        return ($"claude plugin marketplace add {url}", $"\"chat.plugins.marketplaces\": [\"{url}\"]");
    }
}
