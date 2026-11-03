namespace MyRegistry.Domain;

public enum NotificationKind { StatusChanged, NewSubmission, ReportFiled, CommentAdded }

/// <summary>Notification in-app. Le texte est composé à l'affichage (FR/EN) à partir du type, du sujet et du détail.</summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public NotificationKind Kind { get; set; }
    /// <summary>Sujet lisible : « namespace/nom@version ».</summary>
    public string Subject { get; set; } = "";
    public string? Detail { get; set; }
    public string? Link { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

public class NotificationPreference
{
    public string UserId { get; set; } = "";
    public NotificationKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
}
