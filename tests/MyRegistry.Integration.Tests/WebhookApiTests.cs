using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

/// <summary>Récepteur HTTP local : enregistre les requêtes reçues et répond avec le code configuré.</summary>
public sealed class HookReceiver : IDisposable
{
    public sealed record Received(string Body, Dictionary<string, string> Headers);
    private HttpListener _l = null!;
    public ConcurrentQueue<Received> Requests { get; } = new();
    public int Status { get; set; } = 200;
    public string Url { get; }

    public HookReceiver()
    {
        // Un HttpListener qui échoue à démarrer est inutilisable : on en crée un nouveau à chaque essai de port.
        var port = Random.Shared.Next(20000, 40000);
        for (var attempt = 0; ; attempt++, port = Random.Shared.Next(20000, 40000))
        {
            var l = new HttpListener();
            try { l.Prefixes.Add($"http://127.0.0.1:{port}/hook/"); l.Start(); _l = l; break; }
            catch (HttpListenerException) when (attempt < 50) { l.Close(); }
        }
        Url = $"http://127.0.0.1:{port}/hook/";
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            try
            {
                var c = await _l.GetContextAsync();
                using var r = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                var body = await r.ReadToEndAsync();
                Requests.Enqueue(new(body, c.Request.Headers.AllKeys.ToDictionary(k => k!, k => c.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase)));
                c.Response.StatusCode = Status; c.Response.Close();
            }
            catch { /* arrêt */ }
        }
    }

    public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
}

[Collection("db")]
public class WebhookApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email) { var c = db.Factory.CreateClient(); c.DefaultRequestHeaders.Add("X-Dev-User", email); return c; }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');
    private async Task SetRole(string email, string role) => (await Admin().PutAsJsonAsync($"/api/admin/users/{email}", new { role, active = true })).EnsureSuccessStatusCode();

    private async Task<(Guid Id, string? Secret)> Create(string url, string format = "json", params string[] events)
    {
        var res = await Admin().PostAsJsonAsync("/api/admin/webhooks", new { name = U("hook"), format, url, events = events.Length > 0 ? events : ["new_submission"] });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var j = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (j.GetProperty("webhook").GetProperty("id").GetGuid(), j.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
    }

    private Task Delete(Guid id) => Admin().DeleteAsync($"/api/admin/webhooks/{id}");

    private async Task<int> Deliver(DateTime? at = null)
    {
        using var scope = db.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookService>().DeliverDueAsync(at ?? DateTime.UtcNow);
    }

    private async Task Submit(string publisherEmail)
    {
        var name = U("wh");
        (await As(publisherEmail).PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, fileName = "SKILL.md", description = "d",
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}",
        })).EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> Deliveries(Guid id) => await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/webhooks/{id}/deliveries");

    [Fact]
    public async Task Admin_only_validation_masking_and_one_time_secret()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As($"{U("rd")}@example.com").GetAsync("/api/admin/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Admin().PostAsJsonAsync("/api/admin/webhooks", new { name = "x", format = "xml", url = "nope", events = Array.Empty<string>() })).StatusCode);

        using var rx = new HookReceiver();
        var (id, secret) = await Create(rx.Url + "?token=TOPSECRETTOKEN", "json");
        try
        {
            Assert.True(secret!.Length >= 16);
            var list = await Admin().GetStringAsync("/api/admin/webhooks");
            Assert.DoesNotContain(secret, list);
            Assert.DoesNotContain("TOPSECRETTOKEN", list);
            Assert.Contains(id.ToString(), list);

            var rotated = (await (await Admin().PostAsync($"/api/admin/webhooks/{id}/rotate-secret", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString();
            Assert.NotEqual(secret, rotated);
        }
        finally { await Delete(id); }
    }

    [Fact]
    public async Task A_submission_is_delivered_signed_to_subscribed_webhooks_only()
    {
        var pub = $"{U("pub")}@example.com"; await SetRole(pub, "Publisher");
        using var rx = new HookReceiver(); using var other = new HookReceiver();
        var (id, secret) = await Create(rx.Url, "json", "new_submission");
        var (otherId, _) = await Create(other.Url, "json", "report_filed");
        try
        {
            await Submit(pub);
            Assert.True(await Deliver() >= 1);

            var got = Assert.Single(rx.Requests);
            Assert.Equal("new_submission", got.Headers["X-MyRegistry-Event"]);
            Assert.Equal(WebhookRules.Sign(secret!, got.Body), got.Headers["X-MyRegistry-Signature"]);
            var j = JsonDocument.Parse(got.Body).RootElement;
            Assert.Equal("new_submission", j.GetProperty("event").GetString());
            Assert.Equal(pub, j.GetProperty("actor").GetString());
            Assert.StartsWith("myregistry/wh", j.GetProperty("subject").GetString());
            Assert.Empty(other.Requests);
            Assert.Equal("Delivered", (await Deliveries(id)).EnumerateArray().First().GetProperty("status").GetString());
        }
        finally { await Delete(id); await Delete(otherId); }
    }

    [Fact]
    public async Task Teams_format_sends_an_adaptive_card_without_signature()
    {
        var pub = $"{U("pub")}@example.com"; await SetRole(pub, "Publisher");
        using var rx = new HookReceiver();
        var (id, secret) = await Create(rx.Url, "teams", "new_submission");
        try
        {
            Assert.Null(secret);
            await Submit(pub); await Deliver();
            var got = Assert.Single(rx.Requests);
            Assert.False(got.Headers.ContainsKey("X-MyRegistry-Signature"));
            Assert.Equal("application/vnd.microsoft.card.adaptive", JsonDocument.Parse(got.Body).RootElement.GetProperty("attachments")[0].GetProperty("contentType").GetString());
        }
        finally { await Delete(id); }
    }

    [Fact]
    public async Task A_disabled_webhook_receives_nothing_and_a_pending_delivery_to_it_fails()
    {
        var pub = $"{U("pub")}@example.com"; await SetRole(pub, "Publisher");
        using var rx = new HookReceiver();
        var (id, _) = await Create(rx.Url, "json", "new_submission");
        try
        {
            rx.Status = 500;
            await Submit(pub); await Deliver();                                   // 1re tentative en échec -> reste en attente
            var update = await Admin().PutAsJsonAsync($"/api/admin/webhooks/{id}", new { name = "stopped", format = "json", url = rx.Url, events = new[] { "new_submission" }, active = false });
            update.EnsureSuccessStatusCode();
            await Submit(pub);                                                    // désactivé : rien n'est mis en file
            await Deliver(DateTime.UtcNow.AddHours(1));
            var d = (await Deliveries(id)).EnumerateArray().ToList();
            Assert.Single(d);
            Assert.Equal("Failed", d[0].GetProperty("status").GetString());
            Assert.Single(rx.Requests);
        }
        finally { await Delete(id); }
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff_then_succeed()
    {
        var pub = $"{U("pub")}@example.com"; await SetRole(pub, "Publisher");
        using var rx = new HookReceiver { Status = 503 };
        var (id, _) = await Create(rx.Url, "json", "new_submission");
        try
        {
            await Submit(pub);
            await Deliver();
            var d = (await Deliveries(id)).EnumerateArray().Single();
            Assert.Equal("Pending", d.GetProperty("status").GetString());
            Assert.Equal(1, d.GetProperty("attempts").GetInt32());
            Assert.Equal(503, d.GetProperty("lastStatusCode").GetInt32());
            Assert.True(d.GetProperty("nextAttemptAt").GetDateTime() > DateTime.UtcNow);

            await Deliver();                                                      // pas encore échu : aucun renvoi
            Assert.Single(rx.Requests);

            rx.Status = 200;
            await Deliver(DateTime.UtcNow.AddMinutes(2));
            Assert.Equal(2, rx.Requests.Count);
            var after = (await Deliveries(id)).EnumerateArray().Single();
            Assert.Equal("Delivered", after.GetProperty("status").GetString());
            Assert.Equal(2, after.GetProperty("attempts").GetInt32());
        }
        finally { await Delete(id); }
    }

    [Fact]
    public async Task Permanent_refusals_are_not_retried_and_retries_are_capped()
    {
        var pub = $"{U("pub")}@example.com"; await SetRole(pub, "Publisher");
        using var gone = new HookReceiver { Status = 404 }; using var down = new HookReceiver { Status = 500 };
        var (goneId, _) = await Create(gone.Url, "json", "new_submission");
        var (downId, _) = await Create(down.Url, "json", "new_submission");
        try
        {
            await Submit(pub);
            var t = DateTime.UtcNow;
            for (var i = 0; i < 10; i++) { await Deliver(t); t = t.AddDays(1); }
            Assert.Single(gone.Requests);
            Assert.Equal("Failed", (await Deliveries(goneId)).EnumerateArray().Single().GetProperty("status").GetString());
            Assert.Equal(WebhookRules.MaxAttempts, down.Requests.Count);
            Assert.Equal("Failed", (await Deliveries(downId)).EnumerateArray().Single().GetProperty("status").GetString());
        }
        finally { await Delete(goneId); await Delete(downId); }
    }

    [Fact]
    public async Task Test_endpoint_sends_once_synchronously_and_reports_the_result()
    {
        using var rx = new HookReceiver();
        var (id, secret) = await Create(rx.Url, "json");
        try
        {
            var ok = await (await Admin().PostAsync($"/api/admin/webhooks/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Delivered", ok.GetProperty("status").GetString());
            var got = Assert.Single(rx.Requests);
            Assert.Equal("test", got.Headers["X-MyRegistry-Event"]);
            Assert.Equal(WebhookRules.Sign(secret!, got.Body), got.Headers["X-MyRegistry-Signature"]);

            rx.Status = 500;
            var ko = await (await Admin().PostAsync($"/api/admin/webhooks/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Failed", ko.GetProperty("status").GetString());
            Assert.Equal(1, ko.GetProperty("attempts").GetInt32());
            Assert.Equal(HttpStatusCode.NotFound, (await Admin().PostAsync($"/api/admin/webhooks/{Guid.NewGuid()}/test", null)).StatusCode);
        }
        finally { await Delete(id); }
    }

    [Fact]
    public async Task Enabling_and_disabling_is_audited_and_stops_new_events()
    {
        using var rx = new HookReceiver();
        var (id, _) = await Create(rx.Url, "json", "new_submission");
        try
        {
            using var scope = db.Factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<WebhookService>();
            Assert.True(await svc.SetActiveAsync(id, false, "admin@example.com"));
            Assert.False((await svc.ListAsync()).Single(w => w.Id == id).Active);
            Assert.True(await svc.SetActiveAsync(id, true, "admin@example.com"));
            Assert.True((await svc.ListAsync()).Single(w => w.Id == id).Active);
            Assert.False(await svc.SetActiveAsync(Guid.NewGuid(), true, "admin@example.com"));
            Assert.Contains("webhook-disable", await Admin().GetStringAsync("/api/audit?action=webhook-disable&pageSize=5"));
        }
        finally { await Delete(id); }
    }

    [Fact]
    public async Task Deleting_a_webhook_removes_its_deliveries_and_unknown_ids_are_404()
    {
        using var rx = new HookReceiver();
        var (id, _) = await Create(rx.Url, "json");
        await Admin().PostAsync($"/api/admin/webhooks/{id}/test", null);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"/api/admin/webhooks/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/admin/webhooks/{id}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().DeleteAsync($"/api/admin/webhooks/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_delivery_client_refuses_internal_addresses_and_does_not_follow_redirects()
    {
        using var rx = new HookReceiver();
        using var strict = new HttpClient(WebhookService.CreateHandler(allowPrivate: false));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => strict.PostAsync(rx.Url, new StringContent("{}")));
        Assert.Empty(rx.Requests);
        Assert.Contains("interne", ex.ToString());

        using var relaxed = new HttpClient(WebhookService.CreateHandler(allowPrivate: true));
        Assert.True((await relaxed.PostAsync(rx.Url, new StringContent("{}"))).IsSuccessStatusCode);
        Assert.Single(rx.Requests);
    }
}
