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
