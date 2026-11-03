using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record WebhookEvent(string Name, string Subject, string? Detail, string? Link, string Actor, DateTime At);

public static class WebhookEvents
{
    public const string Test = "test";
    public static readonly string[] All = ["status_changed", "new_submission", "report_filed", "comment_added"];

    public static string NameOf(NotificationKind k) => k switch
    {
        NotificationKind.StatusChanged => "status_changed",
        NotificationKind.NewSubmission => "new_submission",
        NotificationKind.ReportFiled => "report_filed",
        _ => "comment_added",
    };
}

public sealed record WebhookInput(string Name, string Format, string Url, IReadOnlyList<string> Events, string? Secret);

public static class WebhookRules
{
    public static readonly string[] Formats = ["teams", "json"];
    public const int MaxAttempts = 6;
    private static readonly int[] DelayMinutes = [1, 5, 30, 120, 720];

    /// <summary>Après l'échec n° <paramref name="failedAttempts"/> : délai avant le prochain essai, ou null si les essais sont épuisés.</summary>
    public static TimeSpan? NextDelay(int failedAttempts) =>
        failedAttempts >= MaxAttempts ? null : TimeSpan.FromMinutes(DelayMinutes[Math.Max(0, failedAttempts - 1)]);

    public static IReadOnlyList<Issue> Validate(WebhookInput i, bool allowPrivate)
    {
        var e = new List<Issue>();
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length is < 2 or > 60) e.Add(new("name", "Le nom doit faire 2 à 60 caractères."));
        if (!Formats.Contains(i.Format)) e.Add(new("format", "Format : teams ou json."));
        if (UrlError(i.Url, allowPrivate) is { } ue) e.Add(new("url", ue));
        if (i.Events.Count == 0) e.Add(new("events", "Choisissez au moins un événement."));
        else if (i.Events.Any(x => !WebhookEvents.All.Contains(x))) e.Add(new("events", $"Événements possibles : {string.Join(", ", WebhookEvents.All)}."));
        if (i.Secret is { Length: > 0 and < 16 }) e.Add(new("secret", "Clé de signature : 16 caractères minimum (ou laissez vide pour en générer une)."));
        return e;
    }

    /// <summary>Anti-SSRF : https obligatoire, pas d'identifiants dans l'URL, pas de cible interne (loopback, réseaux privés, lien local, métadonnées cloud). <paramref name="allowPrivate"/> n'est destiné qu'aux tests et au développement.</summary>
    public static string? UrlError(string? url, bool allowPrivate)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "URL invalide.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "L'URL ne doit pas contenir d'identifiants.";
        if (allowPrivate) return u.Scheme is "http" or "https" ? null : "http ou https attendu.";
        if (u.Scheme != Uri.UriSchemeHttps) return "https obligatoire.";
        var host = u.IdnHost.ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return IsPrivateAddress(ip) ? "Adresse interne refusée." : null;
        if (host is "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan") || !host.Contains('.'))
            return "Nom d'hôte interne refusé.";
        return null;
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC; // fe80::/10, fec0::/10, ff00::/8, fc00::/7
        }
        var v = ip.GetAddressBytes();
        return v[0] == 0 || v[0] == 10 || v[0] >= 224
            || (v[0] == 100 && v[1] is >= 64 and <= 127)         // 100.64.0.0/10 (CGNAT)
            || (v[0] == 169 && v[1] == 254)                      // lien local, dont les métadonnées cloud 169.254.169.254
            || (v[0] == 172 && v[1] is >= 16 and <= 31)
            || (v[0] == 192 && v[1] == 168)
            || (v[0] == 192 && v[1] == 0 && v[2] == 0)
            || (v[0] == 198 && v[1] is 18 or 19);
    }

    /// <summary>URL affichable sans révéler le jeton qu'elle contient souvent (webhooks Teams) : hôte + fin du chemin.</summary>
    public static string MaskUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "***";
        var tail = u.AbsolutePath.Length > 6 ? u.AbsolutePath[^4..] : "";
        return $"{u.Scheme}://{u.Host}/…{tail}";
    }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>En-tête de signature : « sha256=<hex> » (HMAC-SHA256 du corps exact envoyé).</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static string Body(string format, WebhookEvent e, string baseUrl) => format == "teams" ? Teams(e, baseUrl) : Json(e, baseUrl);

    public static string Json(WebhookEvent e, string baseUrl) => new JsonObject
    {
        ["event"] = e.Name, ["subject"] = e.Subject, ["detail"] = e.Detail, ["url"] = Absolute(baseUrl, e.Link),
        ["actor"] = e.Actor, ["at"] = e.At.ToUniversalTime().ToString("o"),
    }.ToJsonString();

    /// <summary>Message pour un webhook « Workflows » de Teams : enveloppe `message` + carte adaptative.</summary>
    public static string Teams(WebhookEvent e, string baseUrl)
    {
        var title = e.Name switch
        {
            "status_changed" => "Statut modifié", "new_submission" => "Nouvelle soumission à modérer", "report_filed" => "Signalement",
            "comment_added" => "Nouveau commentaire", _ => "Test du webhook",
        };
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject { ["type"] = "TextBlock", ["text"] = e.Subject, ["wrap"] = true },
        };
        if (!string.IsNullOrWhiteSpace(e.Detail)) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = e.Detail, ["isSubtle"] = true, ["wrap"] = true });
        body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = $"par {e.Actor}", ["isSubtle"] = true, ["size"] = "Small" });
        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard", ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["version"] = "1.4", ["body"] = body,
        };
        if (Absolute(baseUrl, e.Link) is { } link)
            card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Ouvrir", ["url"] = link });
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card }),
        }.ToJsonString();
    }

    private static string? Absolute(string baseUrl, string? link) =>
        string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(link) ? null : (link.StartsWith('/') ? baseUrl.TrimEnd('/') + link : null);

    /// <summary>Un refus définitif du destinataire (400, 401, 403, 404, 410…) ne sera pas rejoué ; les erreurs serveur, 408 et 429 le seront.</summary>
    public static bool IsRetryable(int? statusCode) => statusCode is null or 408 or 425 or 429 or >= 500;

    public static string Serialize(WebhookEvent e) => JsonSerializer.Serialize(e);
    public static WebhookEvent? Deserialize(string json) => JsonSerializer.Deserialize<WebhookEvent>(json);
}
