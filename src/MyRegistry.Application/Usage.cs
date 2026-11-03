namespace MyRegistry.Application;

public static class UsageSources
{
    public const string Web = "web", Cli = "cli", Npm = "npm", Mcp = "mcp", Api = "api";
    public static readonly string[] All = [Web, Cli, Npm, Mcp, Api];

    /// <summary>Source déduite de l'agent utilisateur : la CLI s'annonce « myregistry-cli/x.y.z » ; un navigateur (Mozilla) est « web » ; le reste est « api ».</summary>
    public static string FromUserAgent(string? ua) =>
        ua is null ? Api
        : ua.StartsWith("myregistry-cli/", StringComparison.OrdinalIgnoreCase) ? Cli
        : ua.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase) ? Web
        : Api;
}

public static class UsageRules
{
    public const int DefaultDays = 30, MaxDays = 365;

    public static int ClampDays(int? days) => days is null or < 1 ? DefaultDays : Math.Min(days.Value, MaxDays);

    /// <summary>Jour UTC (minuit) d'un instant : clé de regroupement des compteurs.</summary>
    public static DateTime DayOf(DateTime utc) => DateTime.SpecifyKind(utc.ToUniversalTime().Date, DateTimeKind.Utc);

    /// <summary>Premier jour inclus d'une fenêtre de <paramref name="days"/> jours se terminant aujourd'hui.</summary>
    public static DateTime WindowStart(DateTime now, int days) => DayOf(now).AddDays(-(ClampDays(days) - 1));

    /// <summary>Série continue (un point par jour, zéro si aucun usage) : sans trous, les graphiques ne mentent pas sur les jours calmes.</summary>
    public static IReadOnlyList<(DateTime Day, int Count)> FillDays(IReadOnlyDictionary<DateTime, int> counts, DateTime now, int days)
    {
        var start = WindowStart(now, days);
        return Enumerable.Range(0, ClampDays(days)).Select(i => (start.AddDays(i), counts.GetValueOrDefault(start.AddDays(i)))).ToList();
    }
}
