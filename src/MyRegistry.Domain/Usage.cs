namespace MyRegistry.Domain;

/// <summary>Compteur quotidien d'usage d'une version, par source (web, cli, npm, mcp, api). Une ligne par jour, version et source.</summary>
public class UsageDaily
{
    public DateTime Day { get; set; }
    public Guid VersionId { get; set; }
    public string Source { get; set; } = "";
    public int Count { get; set; }
}
