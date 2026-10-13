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
});
builder.Services.AddScoped<IAuthProvider, DevAuthProvider>();
builder.Services.AddScoped<Localizer>();
builder.Services.AddScoped<PublishService>();
builder.Services.AddScoped<CatalogService>();
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

app.UseStaticFiles();
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
        user = new { u.Email, Role = u.Role.ToString() },
    };
}).WithName("RegistryInfo");

app.MapApi();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
