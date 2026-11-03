using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class UsageApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email) { var c = db.Factory.CreateClient(); c.DefaultRequestHeaders.Add("X-Dev-User", email); return c; }
    private static string U(string p) => p + "-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private async Task<(string Name, Guid Id)> Publish()
    {
        var name = U("use");
        var res = await Admin().PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "myregistry", name, fileName = "SKILL.md", description = "d", rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}" });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        (await Admin().PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).EnsureSuccessStatusCode();
        return (name, id);
    }

    private async Task<JsonElement> Usage(int days = 30) => await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/usage?days={days}");
    private static int Source(JsonElement u, string s) => u.GetProperty("bySource").GetProperty(s).GetInt32();

    [Fact]
    public async Task Downloads_are_attributed_to_cli_web_api_and_npm_by_how_they_were_made()
    {
        var (name, id) = await Publish();
        var before = await Usage();

        async Task Get(string url, string? ua) { var r = new HttpRequestMessage(HttpMethod.Get, url); if (ua is not null) r.Headers.TryAddWithoutValidation("User-Agent", ua); (await Admin().SendAsync(r)).EnsureSuccessStatusCode(); }
        await Get($"/api/catalog/{id}/download", "myregistry-cli/0.1.0");
        await Get($"/api/catalog/{id}/download", "myregistry-cli/0.1.0");
        await Get($"/api/catalog/{id}/download", "Mozilla/5.0 Chrome/120");
        await Get($"/api/catalog/{id}/archive?target=claude", "curl/8.5");
        var packument = await Admin().GetFromJsonAsync<JsonElement>($"/skills/@myregistry/{name}");
        var tarball = new Uri(packument.GetProperty("versions").GetProperty("1.0.0").GetProperty("dist").GetProperty("tarball").GetString()!).PathAndQuery;
        (await Admin().GetAsync(tarball)).EnsureSuccessStatusCode();

        var after = await Usage();
        Assert.Equal(2, Source(after, "cli") - Source(before, "cli"));
        Assert.Equal(1, Source(after, "web") - Source(before, "web"));
        Assert.Equal(1, Source(after, "api") - Source(before, "api"));
        Assert.Equal(1, Source(after, "npm") - Source(before, "npm"));
        Assert.Equal(5, after.GetProperty("total").GetInt32() - before.GetProperty("total").GetInt32());
        Assert.Contains(after.GetProperty("top").EnumerateArray(), t => t.GetProperty("name").GetString() == $"myregistry/{name}" && t.GetProperty("count").GetInt32() == 5);
        Assert.True(after.GetProperty("byType").GetProperty("Skill").GetInt32() >= 5);
    }

    [Fact]
    public async Task Mcp_reads_are_counted_as_mcp_usage()
    {
        var (name, _) = await Publish();
        var before = Source(await Usage(), "mcp");
        (await Admin().PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "get_artifact", arguments = new { name } } })).EnsureSuccessStatusCode();
        Assert.Equal(before + 1, Source(await Usage(), "mcp"));
    }

    [Fact]
    public async Task Concurrent_first_writes_of_the_day_lose_no_count()
    {
        var (_, id) = await Publish();
        var day = new DateTime(1980, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(new Random().Next(0, 3000));   // jour isolé : aucune autre écriture ne le touche
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var scope = db.Factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<UsageService>().RecordAsync(id, "cli", day);
        }));
        using var check = db.Factory.Services.CreateScope();
        var s = await check.ServiceProvider.GetRequiredService<UsageService>().SummaryAsync(1, day.AddHours(11));
        Assert.Equal(20, s.Daily.Single().Count);
    }

    [Fact]
    public async Task Unknown_sources_are_folded_into_api_and_the_window_is_gap_free()
    {
        var (_, id) = await Publish();
        using var scope = db.Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<UsageService>();
        // Jour aléatoire dans le passé lointain : la base de test persiste d'une exécution à l'autre et ne doit pas fausser le total.
        var now = new DateTime(1990, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddDays(new Random().Next(0, 3000));
        var before = await svc.SummaryAsync(7, now);
        await svc.RecordAsync(id, "n-importe-quoi", now);
        var s = await svc.SummaryAsync(7, now);
        Assert.Equal(7, s.Daily.Count);                                   // une valeur par jour, même sans usage
        Assert.Equal(before.BySource["api"] + 1, s.BySource["api"]);     // source inconnue -> api
        Assert.Equal(before.Total + 1, s.Total);
        Assert.Equal(before.Daily[^1].Count + 1, s.Daily[^1].Count);     // comptée le jour demandé
        Assert.Equal(s.Daily.Sum(d => d.Count), s.Total);
    }

    [Fact]
    public async Task Usage_report_is_for_moderators_and_above()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As($"{U("rd")}@example.com").GetAsync("/api/admin/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync("/api/admin/usage?days=99999")).StatusCode);
        Assert.Equal(365, (await Usage(99999)).GetProperty("days").GetInt32());
    }
}
