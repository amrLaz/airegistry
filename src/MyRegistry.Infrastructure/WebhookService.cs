using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record WebhookOptions(bool AllowPrivate);

public sealed record WebhookDto(Guid Id, string Name, string Format, string MaskedUrl, string[] Events, bool Active, bool Signed, DateTime CreatedAt, int Pending, int Failed, DateTime? LastDeliveredAt);
public sealed record WebhookResult(WebhookDto? Webhook, string? Secret, IReadOnlyList<Issue> Errors, bool NotFound = false);
public sealed record DeliveryDto(Guid Id, string Event, string Status, int Attempts, int? LastStatusCode, string? LastError, DateTime CreatedAt, DateTime? NextAttemptAt, DateTime? DeliveredAt);

public class WebhookService(AppDbContext db, IHttpClientFactory http, WebhookOptions options, SettingsService settings, ILogger<WebhookService> log)
{
    public const string ClientName = "webhooks";

    /// <summary>HttpClient « webhooks » : pas de redirection, délai court, et refus de se connecter à une adresse interne même si le nom DNS a changé depuis la validation.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var allowed = allowPrivate ? addresses : addresses.Where(a => !WebhookRules.IsPrivateAddress(a)).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException("Adresse de destination interne refusée.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    };

    private static string[] Split(string events) => events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<IReadOnlyList<WebhookDto>> ListAsync(CancellationToken ct = default)
    {
        var hooks = await db.Webhooks.AsNoTracking().ToListAsync(ct);
        var deliveries = await db.WebhookDeliveries.AsNoTracking().Select(d => new { d.WebhookId, d.Status, d.DeliveredAt }).ToListAsync(ct);
        return hooks.OrderBy(h => h.CreatedAt).Select(h =>
        {
            var mine = deliveries.Where(d => d.WebhookId == h.Id).ToList();
            return new WebhookDto(h.Id, h.Name, h.Format, WebhookRules.MaskUrl(h.Url), Split(h.Events), h.Active, !string.IsNullOrEmpty(h.Secret), h.CreatedAt,
                mine.Count(d => d.Status == DeliveryStatus.Pending), mine.Count(d => d.Status == DeliveryStatus.Failed), mine.Where(d => d.DeliveredAt != null).Max(d => d.DeliveredAt));
        }).ToList();
    }

    public async Task<WebhookResult> CreateAsync(WebhookInput input, string actor, CancellationToken ct = default)
    {
        var errors = WebhookRules.Validate(input, options.AllowPrivate);
        if (errors.Count > 0) return new(null, null, errors);
        var secret = input.Format == "json" ? (string.IsNullOrEmpty(input.Secret) ? WebhookRules.NewSecret() : input.Secret) : null;
        var h = new Webhook { Name = input.Name.Trim(), Format = input.Format, Url = input.Url.Trim(), Secret = secret, Events = string.Join(',', input.Events.Distinct()), CreatedBy = actor };
        db.Webhooks.Add(h);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "webhook-create", Entity = "Webhook", EntityId = h.Id.ToString(), Details = $"{h.Format} {WebhookRules.MaskUrl(h.Url)}" });
        await db.SaveChangesAsync(ct);
        return new((await ListAsync(ct)).First(x => x.Id == h.Id), secret, []);
    }

    /// <summary>Modifie le nom, l'URL, les événements et l'état. La clé de signature ne change que par <see cref="RotateSecretAsync"/>.</summary>
    public async Task<WebhookResult> UpdateAsync(Guid id, WebhookInput input, bool? active, string actor, CancellationToken ct = default)
    {
        var h = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return new(null, null, [], NotFound: true);
        var errors = WebhookRules.Validate(input with { Format = h.Format, Secret = null }, options.AllowPrivate);
        if (errors.Count > 0) return new(null, null, errors);
        h.Name = input.Name.Trim(); h.Url = input.Url.Trim(); h.Events = string.Join(',', input.Events.Distinct());
        if (active is not null) h.Active = active.Value;
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "webhook-update", Entity = "Webhook", EntityId = id.ToString(), Details = WebhookRules.MaskUrl(h.Url) });
        await db.SaveChangesAsync(ct);
        return new((await ListAsync(ct)).First(x => x.Id == id), null, []);
    }

    public async Task<bool> SetActiveAsync(Guid id, bool active, string actor, CancellationToken ct = default)
    {
        var h = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return false;
        h.Active = active;
        db.Audit.Add(new AuditEntry { UserId = actor, Action = active ? "webhook-enable" : "webhook-disable", Entity = "Webhook", EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string?> RotateSecretAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var h = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id && x.Format == "json", ct);
        if (h is null) return null;
        h.Secret = WebhookRules.NewSecret();
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "webhook-rotate-secret", Entity = "Webhook", EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
        return h.Secret;
    }

    public async Task<bool> DeleteAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var h = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return false;
        db.WebhookDeliveries.RemoveRange(db.WebhookDeliveries.Where(d => d.WebhookId == id));
        db.Webhooks.Remove(h);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "webhook-delete", Entity = "Webhook", EntityId = id.ToString(), Details = h.Name });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Met en file une livraison par webhook actif abonné. N'enregistre pas : l'appelant sauvegarde avec sa transaction (pas d'événement sans action).</summary>
    public async Task EnqueueAsync(WebhookEvent e, CancellationToken ct = default)
    {
        var hooks = await db.Webhooks.AsNoTracking().Where(h => h.Active).ToListAsync(ct);
        foreach (var h in hooks.Where(h => Split(h.Events).Contains(e.Name)))
            db.WebhookDeliveries.Add(new WebhookDelivery { WebhookId = h.Id, Event = e.Name, Payload = WebhookRules.Serialize(e) });
    }

    public async Task<IReadOnlyList<DeliveryDto>?> DeliveriesAsync(Guid id, int take = 50, CancellationToken ct = default)
    {
        if (!await db.Webhooks.AnyAsync(h => h.Id == id, ct)) return null;
        var rows = await db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == id).ToListAsync(ct);
        return rows.OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(take, 1, 200))
            .Select(d => new DeliveryDto(d.Id, d.Event, d.Status.ToString(), d.Attempts, d.LastStatusCode, d.LastError, d.CreatedAt, d.Status == DeliveryStatus.Pending ? d.NextAttemptAt : null, d.DeliveredAt)).ToList();
    }

    /// <summary>Envoie un événement de test, tout de suite et une seule fois (pas de reprise), et renvoie le résultat.</summary>
    public async Task<DeliveryDto?> TestAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var h = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return null;
        var d = new WebhookDelivery { WebhookId = id, Event = WebhookEvents.Test, Payload = WebhookRules.Serialize(new WebhookEvent(WebhookEvents.Test, "Test du webhook « " + h.Name + " »", "Si vous lisez ceci, la livraison fonctionne.", null, actor, DateTime.UtcNow)) };
        db.WebhookDeliveries.Add(d);
        await db.SaveChangesAsync(ct);
        await AttemptAsync(h, d, await BaseUrlAsync(ct), allowRetry: false, ct);
        await db.SaveChangesAsync(ct);
        return new(d.Id, d.Event, d.Status.ToString(), d.Attempts, d.LastStatusCode, d.LastError, d.CreatedAt, null, d.DeliveredAt);
    }

    private async Task<string> BaseUrlAsync(CancellationToken ct) => await settings.GetAsync(SettingKeys.PublicUrl, "", ct);

    /// <summary>Livre les envois échus (appelé par le job de fond, déjà sous verrou). Renvoie le nombre de tentatives.</summary>
    public async Task<int> DeliverDueAsync(DateTime now, int max = 50, CancellationToken ct = default)
    {
        var due = (await db.WebhookDeliveries.Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now).ToListAsync(ct))
            .OrderBy(d => d.NextAttemptAt).Take(max).ToList();
        if (due.Count == 0) return 0;
        var hooks = await db.Webhooks.Where(h => due.Select(d => d.WebhookId).Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var baseUrl = await BaseUrlAsync(ct);
        foreach (var d in due)
        {
            if (!hooks.TryGetValue(d.WebhookId, out var h) || !h.Active) { d.Status = DeliveryStatus.Failed; d.LastError = "Webhook supprimé ou désactivé."; continue; }
            await AttemptAsync(h, d, baseUrl, allowRetry: true, ct);
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task AttemptAsync(Webhook h, WebhookDelivery d, string baseUrl, bool allowRetry, CancellationToken ct)
    {
        d.Attempts++;
        int? status = null; string? error = null;
        try
        {
            var evt = WebhookRules.Deserialize(d.Payload) ?? throw new InvalidOperationException("Événement illisible.");
            var body = WebhookRules.Body(h.Format, evt, baseUrl);
            using var req = new HttpRequestMessage(HttpMethod.Post, h.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("User-Agent", "MyRegistry-Webhooks/1");
            req.Headers.TryAddWithoutValidation("X-MyRegistry-Event", d.Event);
            req.Headers.TryAddWithoutValidation("X-MyRegistry-Delivery", d.Id.ToString());
            if (!string.IsNullOrEmpty(h.Secret)) req.Headers.TryAddWithoutValidation("X-MyRegistry-Signature", WebhookRules.Sign(h.Secret, body));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var res = await http.CreateClient(ClientName).SendAsync(req, cts.Token);
            status = (int)res.StatusCode;
            if (res.IsSuccessStatusCode) { d.Status = DeliveryStatus.Delivered; d.DeliveredAt = DateTime.UtcNow; d.LastStatusCode = status; d.LastError = null; return; }
            error = $"Réponse HTTP {status}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            error = ex is TaskCanceledException ? "Délai dépassé." : Truncate(ex.Message);
        }
        d.LastStatusCode = status; d.LastError = error;
        var delay = allowRetry && WebhookRules.IsRetryable(status) ? WebhookRules.NextDelay(d.Attempts) : null;
        if (delay is null) d.Status = DeliveryStatus.Failed;
        else d.NextAttemptAt = DateTime.UtcNow + delay.Value;
        log.LogWarning("Webhook {Webhook} : livraison {Delivery} en échec (essai {Attempt}) : {Error}", h.Name, d.Id, d.Attempts, error);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    /// <summary>Supprime les livraisons terminées : réussies après 30 jours, échouées après 90.</summary>
    public async Task<int> PurgeAsync(DateTime now, CancellationToken ct = default) =>
        await db.WebhookDeliveries.Where(d => (d.Status == DeliveryStatus.Delivered && d.CreatedAt < now.AddDays(-30)) || (d.Status == DeliveryStatus.Failed && d.CreatedAt < now.AddDays(-90))).ExecuteDeleteAsync(ct);
}

/// <summary>Livre les webhooks en attente chaque minute (reprises incluses) et purge les anciennes livraisons.</summary>
public sealed class WebhookDeliveryJob : IBackgroundJob
{
    public string Name => "webhook-delivery";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<WebhookService>();
        await svc.DeliverDueAsync(DateTime.UtcNow, 100, ct);
        await svc.PurgeAsync(DateTime.UtcNow, ct);
    }
}
