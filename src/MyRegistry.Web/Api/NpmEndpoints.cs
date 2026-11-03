using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class NpmEndpoints
{
    public static void MapNpmApi(this WebApplication app)
    {
        var npm = app.MapGroup("/skills").WithTags("Surface npm");

        // `npm view @ns/nom` demande `/@ns%2fnom` : on accepte aussi cette forme à un seul segment.
        npm.MapGet("/@{scope}/{name}", (string scope, string name, CatalogService svc, SettingsService settings, HttpContext ctx) =>
            Packument(scope, name, svc, settings, ctx)).WithName("NpmPackument");
        npm.MapGet("/{scoped}", (string scoped, CatalogService svc, SettingsService settings, HttpContext ctx) =>
        {
            var parts = Uri.UnescapeDataString(scoped).TrimStart('@').Split('/', 2);
            return parts.Length == 2 && scoped.StartsWith('@') ? Packument(parts[0], parts[1], svc, settings, ctx) : Task.FromResult(Results.NotFound());
        }).ExcludeFromDescription();

        npm.MapGet("/@{scope}/{name}/-/{file}", async (string scope, string name, string file, CatalogService svc) =>
        {
            if (NpmSurface.VersionFromFile(name, file) is not { } version) return Results.NotFound();
            var v = (await svc.ApprovedVersionsAsync(scope, name, ArtifactType.Skill)).FirstOrDefault(x => x.Version == version);
            if (v is null) return Results.NotFound();
            await svc.CountDownloadAsync(v.Id, UsageSources.Npm);
            return Results.File(Tarball(v), "application/octet-stream", file);
        }).WithName("NpmTarball");
    }

    private static byte[] Tarball(ArtifactVersion v) =>
        NpmSurface.Tarball(v.Artifact.Namespace, v.Artifact.Name, v.Version, v.Description, v.Files.Select(f => (f.Path, f.Content)));

    private static async Task<IResult> Packument(string scope, string name, CatalogService svc, SettingsService settings, HttpContext ctx)
    {
        var (ns, resolved) = await svc.ResolveAliasAsync(scope, name);
        var versions = await svc.ApprovedVersionsAsync(ns, resolved, ArtifactType.Skill);
        if (versions.Count == 0) return Results.NotFound(new { error = "Not found" });

        var configured = await settings.GetAsync(SettingKeys.PublicUrl, "");
        var baseUrl = (configured.Length > 0 ? configured : $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
        var byVersion = versions.ToDictionary(v => v.Version);
        var doc = NpmSurface.Packument(ns, resolved, versions.Select(v => new NpmVersion(v.Version, v.Description, v.CreatedAt)).ToList(), nv =>
        {
            var (sha, integrity) = NpmSurface.Digests(Tarball(byVersion[nv.Version]));
            return ($"{baseUrl}/skills/@{ns}/{resolved}/-/{NpmSurface.TarballFile(resolved, nv.Version)}", sha, integrity);
        });
        return Results.Content(doc.ToJsonString(), "application/json");
    }
}
