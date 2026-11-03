using System.Net;
using System.Text.Json.Nodes;
using MyRegistry.Application;

namespace MyRegistry.Domain.Tests.Rules;

public class WebhookRulesTests
{
    private static readonly DateTime T = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static WebhookEvent E(string? link = "/artifact/1") => new("status_changed", "ns/nom@1.0.0", "Approved", link, "amr@x.com", T);

    [Theory]
    [InlineData("https://example.webhook.office.com/webhookb2/abc", true)]
    [InlineData("https://hooks.example.com/x?y=1", true)]
    [InlineData("http://example.com/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://api.localhost/x", false)]
    [InlineData("https://intranet/x", false)]
    [InlineData("https://svc.internal/x", false)]
    [InlineData("https://host.local/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://10.1.2.3/x", false)]
    [InlineData("https://192.168.0.5/x", false)]
    [InlineData("https://172.16.0.1/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://[::1]/x", false)]
    [InlineData("https://[fd00::1]/x", false)]
    [InlineData("https://[::ffff:10.0.0.1]/x", false)]
    [InlineData("https://user:pw@example.com/x", false)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData("pas une url", false)]
    [InlineData("", false)]
    public void Url_safety(string url, bool ok) => Assert.Equal(ok, WebhookRules.UrlError(url, allowPrivate: false) is null);

    [Fact] public void Private_targets_are_only_allowed_with_the_explicit_development_switch()
    {
        Assert.Null(WebhookRules.UrlError("http://127.0.0.1:5000/hook", allowPrivate: true));
        Assert.NotNull(WebhookRules.UrlError("ftp://127.0.0.1/hook", allowPrivate: true));
        Assert.NotNull(WebhookRules.UrlError("http://user:pw@127.0.0.1/hook", allowPrivate: true));
    }

    [Theory]
    [InlineData("8.8.8.8", false)] [InlineData("1.1.1.1", false)] [InlineData("172.15.0.1", false)] [InlineData("172.32.0.1", false)] [InlineData("100.63.0.1", false)]
    [InlineData("100.64.0.1", true)] [InlineData("172.31.255.255", true)] [InlineData("0.0.0.0", true)] [InlineData("224.0.0.1", true)] [InlineData("198.18.0.1", true)]
    [InlineData("2001:4860:4860::8888", false)] [InlineData("fe80::1", true)] [InlineData("fc00::1", true)]
    public void Private_address_ranges(string ip, bool isPrivate) => Assert.Equal(isPrivate, WebhookRules.IsPrivateAddress(IPAddress.Parse(ip)));

    private static WebhookInput In(string name = "Teams modération", string format = "teams", string url = "https://example.com/hook", string[]? events = null, string? secret = null) =>
        new(name, format, url, events ?? ["new_submission"], secret);

    [Fact] public void A_valid_input_has_no_error() => Assert.Empty(WebhookRules.Validate(In(), false));

    [Theory]
    [InlineData("name")] [InlineData("format")] [InlineData("url")] [InlineData("events")] [InlineData("secret")]
    public void Each_invalid_field_is_reported(string field)
    {
        var bad = field switch
        {
            "name" => In(name: "x"), "format" => In(format: "xml"), "url" => In(url: "http://example.com"),
            "events" => In(events: []), _ => In(secret: "court"),
        };
        Assert.Contains(WebhookRules.Validate(bad, false), x => x.Code == field);
    }

    [Fact] public void Unknown_events_are_refused() => Assert.Contains(WebhookRules.Validate(In(events: ["boom"]), false), x => x.Code == "events");

    [Fact] public void Retry_schedule_backs_off_then_gives_up()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), WebhookRules.NextDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(5), WebhookRules.NextDelay(2));
        Assert.Equal(TimeSpan.FromHours(2), WebhookRules.NextDelay(4));
        Assert.Equal(TimeSpan.FromHours(12), WebhookRules.NextDelay(5));
        Assert.Null(WebhookRules.NextDelay(6));
        Assert.Null(WebhookRules.NextDelay(9));
    }

    [Fact] public void Signature_is_hmac_sha256_of_the_exact_body()
    {
        // Vecteur de référence RFC 4231 (cas 2) : clé « Jefe », message « what do ya want for nothing? ».
        Assert.Equal("sha256=5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", WebhookRules.Sign("Jefe", "what do ya want for nothing?"));
        Assert.NotEqual(WebhookRules.Sign("k", "a"), WebhookRules.Sign("k", "b"));
    }

    [Fact] public void Generated_secrets_are_long_unique_and_accepted_by_validation()
    {
        var a = WebhookRules.NewSecret(); var b = WebhookRules.NewSecret();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 16);
        Assert.Empty(WebhookRules.Validate(In(format: "json", secret: a), false));
    }

    [Fact] public void Json_payload_has_an_absolute_link_and_utc_timestamp()
    {
        var j = JsonNode.Parse(WebhookRules.Json(E(), "https://reg.example.com/"))!;
        Assert.Equal("status_changed", (string?)j["event"]);
        Assert.Equal("https://reg.example.com/artifact/1", (string?)j["url"]);
        Assert.Equal("2026-01-02T03:04:05.0000000Z", (string?)j["at"]);
        Assert.Null((string?)JsonNode.Parse(WebhookRules.Json(E(link: null), "https://reg.example.com"))!["url"]);
    }

    [Fact] public void Json_payload_never_turns_an_arbitrary_link_into_a_url()
    {
        Assert.Null((string?)JsonNode.Parse(WebhookRules.Json(E(link: "https://evil.example.com"), "https://reg.example.com"))!["url"]);
        // Un lien protocole-relatif reste collé à l'hôte du registre : il ne peut pas pointer ailleurs.
        Assert.StartsWith("https://reg.example.com/", (string?)JsonNode.Parse(WebhookRules.Json(E(link: "//evil.example.com"), "https://reg.example.com"))!["url"]);
    }

    [Fact] public void Teams_payload_is_an_adaptive_card_message_with_an_open_action()
    {
        var j = JsonNode.Parse(WebhookRules.Teams(E(), "https://reg.example.com"))!;
        Assert.Equal("message", (string?)j["type"]);
        var att = j["attachments"]![0]!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)att["contentType"]);
        var card = att["content"]!;
        Assert.Equal("AdaptiveCard", (string?)card["type"]);
        Assert.Contains("ns/nom@1.0.0", card["body"]!.ToJsonString());
        Assert.Equal("https://reg.example.com/artifact/1", (string?)card["actions"]![0]!["url"]);
        Assert.Null(JsonNode.Parse(WebhookRules.Teams(E(link: null), "https://reg.example.com"))!["attachments"]![0]!["content"]!["actions"]);
    }

    [Theory]
    [InlineData(null, true)] [InlineData(408, true)] [InlineData(429, true)] [InlineData(500, true)] [InlineData(503, true)]
    [InlineData(400, false)] [InlineData(401, false)] [InlineData(403, false)] [InlineData(404, false)] [InlineData(410, false)]
    public void Only_transient_failures_are_retried(int? status, bool retry) => Assert.Equal(retry, WebhookRules.IsRetryable(status));

    [Fact] public void Format_selects_the_body() =>
        Assert.Contains("\"attachments\"", WebhookRules.Body("teams", E(), "https://r"));

    [Fact] public void Events_round_trip_through_storage()
    {
        var back = WebhookRules.Deserialize(WebhookRules.Serialize(E()))!;
        Assert.Equal(E(), back);
    }

    [Fact] public void Urls_are_masked_for_display()
    {
        var masked = WebhookRules.MaskUrl("https://prod.westeurope.logic.azure.com:443/workflows/abcdef0123456789/triggers/manual/paths/invoke?sig=SECRET");
        Assert.DoesNotContain("SECRET", masked);
        Assert.DoesNotContain("abcdef0123456789", masked);
        Assert.StartsWith("https://prod.westeurope.logic.azure.com/", masked);
    }
}
