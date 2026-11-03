namespace MyRegistry.Domain;

public class Webhook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>« teams » (carte adaptative pour un webhook Workflows) ou « json » (charge signée HMAC).</summary>
    public string Format { get; set; } = "json";
    /// <summary>Clé de signature HMAC-SHA256 (format json). Stockée en clair car nécessaire pour signer ; jamais renvoyée par l'API après la création.</summary>
    public string? Secret { get; set; }
    /// <summary>Événements souscrits, séparés par des virgules (voir WebhookEvents).</summary>
    public string Events { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

public enum DeliveryStatus { Pending, Delivered, Failed }

public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WebhookId { get; set; }
    public string Event { get; set; } = "";
    /// <summary>Événement sérialisé ; la charge envoyée est construite à l'envoi selon le format du webhook.</summary>
    public string Payload { get; set; } = "";
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
}
