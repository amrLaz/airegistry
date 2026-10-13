namespace MyRegistry.Domain;

public enum Origin { Internal, External }

public class Artifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";
    public ArtifactType Type { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ArtifactVersion> Versions { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<Vote> Votes { get; set; } = [];
}

public class ArtifactVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = null!;
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public VersionStatus Status { get; set; } = VersionStatus.Pending;
    public bool Quarantined { get; set; }
    public string ContentHash { get; set; } = "";
    public string QualityGrade { get; set; } = "A";
    public Origin Origin { get; set; } = Origin.Internal;
    public string? MainFile { get; set; }
    /// <summary>Provenance Git (versions importées d'un feed).</summary>
    public Guid? FeedId { get; set; }
    public string? GitPath { get; set; }
    public string? CommitSha { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int DownloadCount { get; set; }
    public List<VersionFile> Files { get; set; } = [];
    public List<StatusHistory> History { get; set; } = [];
}

public class VersionFile
{
    public Guid VersionId { get; set; }
    public string Path { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public bool IsBinary { get; set; }
}

/// <summary>Tag partagé par famille (type d'artefact).</summary>
public class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ArtifactType Type { get; set; }
    public string Name { get; set; } = "";
    public List<Artifact> Artifacts { get; set; } = [];
}

public class Vote
{
    public Guid ArtifactId { get; set; }
    public string UserId { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public class Report
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VersionId { get; set; }
    public string UserId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Details { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArtifactId { get; set; }
    public string UserId { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Hidden { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public class StatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VersionId { get; set; }
    public VersionStatus? From { get; set; }
    public VersionStatus To { get; set; }
    public string UserId { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

public class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? Details { get; set; }
}

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Role Role { get; set; } = Role.Reader;
    public bool Active { get; set; } = true;
    public DateTime? LastLogin { get; set; }
}

/// <summary>Contrat de gouvernance d'un artefact (une famille).</summary>
public class Governance
{
    public Guid ArtifactId { get; set; }
    public string OwnerId { get; set; } = "";
    public RiskLevel Risk { get; set; } = RiskLevel.Low;
    public DataClass DataClass { get; set; } = DataClass.Internal;
    public int? ReviewEveryDays { get; set; }
    public DateTime? LastReviewed { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "";
}

/// <summary>Brouillon de plugin dans l'éditeur (composition non encore publiée).</summary>
public class PluginDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Version { get; set; }
    /// <summary>Composants épinglés, sérialisés en JSON : [{type, slug, version}].</summary>
    public string ComponentsJson { get; set; } = "[]";
    public Guid? PublishedVersionId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Composant épinglé d'une version de plugin publiée (docs/02 : plugin_components).</summary>
public class PluginComponent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PluginVersionId { get; set; }
    public ArtifactType Type { get; set; }
    public string Slug { get; set; } = "";
    public string ExactVersion { get; set; } = "";
    public bool Resolved { get; set; }
}

public enum FeedStatus { Idle, Ok, Failed, Paused }

public class Feed
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Local";
    public string Url { get; set; } = "";
    public string Branch { get; set; } = "main";
    /// <summary>NOM d'une variable d'environnement contenant le jeton (jamais le jeton lui-même).</summary>
    public string? PatRef { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public bool Active { get; set; } = true;
    public bool IsSystem { get; set; }
    public string? NamespaceOverride { get; set; }
    public FeedStatus Status { get; set; } = FeedStatus.Idle;
    public DateTime? LastSyncAt { get; set; }
    public string? LastCommit { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class FeedSyncReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FeedId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public bool Success { get; set; }
    public string? Commit { get; set; }
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Deprecated { get; set; }
    public int Quarantined { get; set; }
    /// <summary>Messages (une ligne chacun) : erreurs et éléments ignorés.</summary>
    public string Messages { get; set; } = "";
}
