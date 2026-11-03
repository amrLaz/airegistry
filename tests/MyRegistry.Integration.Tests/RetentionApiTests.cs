using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

/// <summary>La rétention agit sur toute la base et son réglage est global : ces tests restent dans une classe à part, exécutée séquentiellement dans la collection « db », et remettent le réglage à 0.</summary>
[Collection("db")]
public class RetentionApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email) { var c = db.Factory.CreateClient(); c.DefaultRequestHeaders.Add("X-Dev-User", email); return c; }
    private static string U(string p) => p + "-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private async Task<Guid> Publish(string name, string version)
    {
        var res = await Admin().PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "myregistry", name, version, fileName = "SKILL.md", description = "d", rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name} {version}" });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
    }

    private async Task Set(Guid id, string status) => (await Admin().PutAsJsonAsync($"/api/catalog/{id}/status", new { status })).EnsureSuccessStatusCode();

    private async Task Backdate(params Guid[] ids)
    {
        using var scope = db.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ctx.Versions.Where(v => ids.Contains(v.Id)).ExecuteUpdateAsync(s => s.SetProperty(v => v.CreatedAt, v => v.CreatedAt.AddDays(-400)));
    }

    private Task Configure(int keep, int age = 30) => Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["retention_keep_versions"] = keep.ToString(), ["retention_min_age_days"] = age.ToString() });

    private async Task<JsonElement> Preview() => await Admin().GetFromJsonAsync<JsonElement>("/api/admin/retention");
    private static IEnumerable<string> Names(JsonElement p, string fam) =>
        p.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("artifact").GetString() == $"myregistry/{fam}").Select(i => i.GetProperty("version").GetString()!);

    [Fact]
    public async Task Disabled_by_default_and_admin_only_with_a_required_confirmation()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As($"{U("rd")}@example.com").GetAsync("/api/admin/retention")).StatusCode);
        await Configure(0);
        Assert.False((await Preview()).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync("/api/admin/retention/run", new { confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsync("/api/admin/retention/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["retention_min_age_days"] = "1" })).StatusCode);
    }

    [Fact]
    public async Task Old_deprecated_versions_are_previewed_then_deleted_and_everything_else_survives()
    {
        var fam = U("ret");
        var v1 = await Publish(fam, "1.0.0"); var v2 = await Publish(fam, "1.1.0"); var v3 = await Publish(fam, "1.2.0"); var v4 = await Publish(fam, "2.0.0");
        await Set(v1, "Deprecated"); await Set(v2, "Deprecated"); await Set(v3, "Deprecated"); await Set(v4, "Approved");
        await Backdate(v1, v2, v3);
        try
        {
            await Configure(keep: 2);
            var p = await Preview();
            Assert.True(p.GetProperty("enabled").GetBoolean());
            Assert.Equal(["1.0.0", "1.1.0"], Names(p, fam).OrderBy(x => x));       // 2.0.0 (approuvée) et 1.2.0 (2e plus récente) restent

            var before = await Admin().GetStringAsync($"/api/catalog/{v1}");
            Assert.Contains(fam, before);                                          // l'aperçu n'a rien supprimé

            var run = await (await Admin().PostAsJsonAsync("/api/admin/retention/run", new { confirm = true })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(run.GetProperty("deleted").GetInt32() >= 2);
            Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/catalog/{v1}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/catalog/{v2}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/catalog/{v3}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/catalog/{v4}")).StatusCode);
            Assert.Contains("retention-delete", await Admin().GetStringAsync("/api/audit?action=retention-delete&pageSize=5"));
            Assert.Empty(Names(await Preview(), fam));
        }
        finally { await Configure(0); }
    }

    [Fact]
    public async Task A_version_pinned_by_a_plugin_is_never_deleted()
    {
        var fam = U("pin");
        var old = await Publish(fam, "1.0.0"); var mid = await Publish(fam, "1.1.0"); var cur = await Publish(fam, "2.0.0");
        await Set(old, "Deprecated"); await Set(mid, "Deprecated"); await Set(cur, "Approved");
        await Backdate(old, mid);
        using (var scope = db.Factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ctx.PluginComponents.Add(new PluginComponent { PluginVersionId = Guid.NewGuid(), Type = ArtifactType.Skill, Slug = $"myregistry/{fam}", ExactVersion = "1.0.0", Resolved = true });
            await ctx.SaveChangesAsync();
        }
        try
        {
            await Configure(keep: 1);
            var pruned = Names(await Preview(), fam).ToList();
            Assert.DoesNotContain("1.0.0", pruned);                                          // épinglée par un plugin : gardée malgré son âge
            Assert.Contains("1.1.0", pruned);                                                // même état, non épinglée : supprimable
            Assert.DoesNotContain("2.0.0", pruned);
        }
        finally { await Configure(0); }
    }
}
