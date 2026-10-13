using MyRegistry.Domain;

namespace MyRegistry.Web.Services;

public static class TypeStyle
{
    public static string Color(ArtifactType t) => t switch
    {
        ArtifactType.Skill => "#0F9D8A", ArtifactType.McpServer => "#4F46E5", ArtifactType.Agent => "#9333EA",
        ArtifactType.Prompt => "#B45309", ArtifactType.Plugin => "#E11D74", _ => "#64748B",
    };

    public static string Label(ArtifactType t, bool fr) => t switch
    {
        ArtifactType.McpServer => fr ? "Serveur MCP" : "MCP server",
        ArtifactType.Skill => "Skill", ArtifactType.Agent => "Agent", ArtifactType.Prompt => "Prompt",
        ArtifactType.Plugin => "Plugin", _ => "Hook",
    };

    public static MudBlazor.Color StatusColor(VersionStatus s) => s switch
    {
        VersionStatus.Approved => MudBlazor.Color.Success, VersionStatus.Pending => MudBlazor.Color.Warning,
        VersionStatus.ToReview => MudBlazor.Color.Error, _ => MudBlazor.Color.Default,
    };
}
