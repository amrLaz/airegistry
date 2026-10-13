using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;
using MyRegistry.Web.Api;
using MyRegistry.Web.Components;
using MyRegistry.Web.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Config 12-factor : MYREGISTRY_DB_CONNECTION -> DB_CONNECTION, etc.
builder.Configuration.AddEnvironmentVariables("MYREGISTRY_");

// Sans MYREGISTRY_DB_CONNECTION : mode local SQLite (fichier jetable). Sinon PostgreSQL.
var connection = builder.Configuration["DB_CONNECTION"];
if (string.IsNullOrWhiteSpace(connection))
{
    var path = builder.Configuration["LOCAL_DB_PATH"] ?? "myregistry-local.db";
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={path}"));
}
else builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));

builder.Services.AddSingleton(new DevAuthOptions
{
    Email = builder.Configuration["DEV_USER_EMAIL"] ?? "dev@example.com",
    Role = Enum.TryParse<Role>(builder.Configuration["DEV_USER_ROLE"], true, out var r) ? r : Role.Admin,
    AllowImpersonation = !string.Equals(builder.Configuration["AUTH_MODE"], "oidc", StringComparison.OrdinalIgnoreCase),
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserDirectory, UserDirectory>();
builder.Services.AddScoped<IAuthProvider, HttpDevAuthProvider>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<GovernanceService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddSingleton<MarketplaceCache>();
builder.Services.AddScoped<PluginService>();
builder.Services.AddSingleton<LocalGitProvider>();
builder.Services.AddSingleton<IGitProvider>(sp => sp.GetRequiredService<LocalGitProvider>());
builder.Services.AddHttpClient<GitHubProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<AzureDevOpsProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IGitProviderResolver, GitProviderResolver>();
builder.Services.AddScoped<FeedService>();
builder.Services.AddScoped<PullRequestWorkflow>();
builder.Services.AddSingleton<IJobLock>(_ => string.IsNullOrWhiteSpace(connection) ? new InProcessLock() : new PostgresAdvisoryLock(connection));
builder.Services.AddSingleton<IBackgroundJob, FeedSyncJob>();
builder.Services.AddSingleton<IBackgroundJob, SnapshotJob>();
builder.Services.AddSingleton<JobExecutor>();
// Les jobs ne tournent pas en test ni si MYREGISTRY_JOBS=off (ex. un réplica dédié à l'interface).
if (!builder.Environment.IsEnvironment("Testing") && !string.Equals(builder.Configuration["JOBS"], "off", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<BackgroundJobRunner>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddSingleton<SettingsCache>();
builder.Services.AddScoped<Localizer>();
builder.Services.AddScoped<PublishService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CommunityService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("db", tags: ["ready"]);
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    await Seeder.MigrateAndSeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

app.MapStaticAssets();
app.UseAntiforgery();

// Liveness : sans dépendance. Readiness : vérifie la base.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health");
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/api/registry-info", async (AppDbContext db, IAuthProvider auth) =>
{
    var s = await db.Settings.ToDictionaryAsync(x => x.Key, x => x.Value);
    var u = auth.GetCurrentUser();
    return new
    {
        title = s.GetValueOrDefault(SettingKeys.Title),
        defaultNamespace = s.GetValueOrDefault(SettingKeys.DefaultNamespace),
        publicUrl = s.GetValueOrDefault(SettingKeys.PublicUrl),
        // Dépôt Git du snapshot de la marketplace (lu par VS Code, Claude Code, Codex, Cursor).
        marketplaceRepo = MyRegistry.Application.RepoLocation.ToLocalPath(s.GetValueOrDefault(SettingKeys.MarketplaceRepo)) is { } lp
            ? MyRegistry.Application.RepoLocation.ToFileUrl(lp) : null,
        user = new { u.Email, Role = u.Role.ToString() },
    };
}).WithName("RegistryInfo");

app.MapApi();
app.MapAdminApi();
app.MapPluginApi();
app.MapFeedApi();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
