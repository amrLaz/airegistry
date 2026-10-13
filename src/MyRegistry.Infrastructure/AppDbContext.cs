using Microsoft.EntityFrameworkCore;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactVersion> Versions => Set<ArtifactVersion>();
    public DbSet<VersionFile> Files => Set<VersionFile>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<StatusHistory> StatusHistory => Set<StatusHistory>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Governance> Governances => Set<Governance>();
    public DbSet<PluginDraft> PluginDrafts => Set<PluginDraft>();
    public DbSet<PluginComponent> PluginComponents => Set<PluginComponent>();
    public DbSet<Feed> Feeds => Set<Feed>();
    public DbSet<NamespaceAlias> Aliases => Set<NamespaceAlias>();
    public DbSet<FeedSyncReport> FeedReports => Set<FeedSyncReport>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(100);
            e.Property(x => x.Value).HasColumnName("value");
        });

        b.Entity<Artifact>(e =>
        {
            e.ToTable("artifacts");
            e.HasIndex(x => new { x.Namespace, x.Name, x.Type }).IsUnique();
            e.Property(x => x.Namespace).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(48);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Versions).WithOne(v => v.Artifact).HasForeignKey(v => v.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithMany(t => t.Artifacts).UsingEntity("artifact_tags");
            e.HasMany(x => x.Votes).WithOne().HasForeignKey(v => v.ArtifactId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ArtifactVersion>(e =>
        {
            e.ToTable("artifact_versions");
            e.HasIndex(x => new { x.ArtifactId, x.Version }).IsUnique();
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.FeedId);
            e.Property(x => x.Version).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Origin).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.QualityGrade).HasMaxLength(1);
            e.HasMany(x => x.Files).WithOne().HasForeignKey(f => f.VersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.History).WithOne().HasForeignKey(h => h.VersionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VersionFile>(e =>
        {
            e.ToTable("version_files");
            e.HasKey(x => new { x.VersionId, x.Path });
        });

        b.Entity<Tag>(e =>
        {
            e.ToTable("tags");
            e.HasIndex(x => new { x.Type, x.Name }).IsUnique();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(40);
        });

        b.Entity<Vote>(e => { e.ToTable("votes"); e.HasKey(x => new { x.ArtifactId, x.UserId }); });
        b.Entity<Report>().ToTable("reports");
        b.Entity<Comment>().ToTable("comments");
        b.Entity<StatusHistory>(e =>
        {
            e.ToTable("status_history");
            e.Property(x => x.From).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.To).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<PluginDraft>(e =>
        {
            e.ToTable("plugin_drafts");
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.OwnerId).HasMaxLength(200);
            e.Property(x => x.Namespace).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(48);
        });
        b.Entity<Feed>(e =>
        {
            e.ToTable("feeds");
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.Branch).HasMaxLength(100);
            e.Property(x => x.PatRef).HasMaxLength(80);
            e.Property(x => x.NamespaceOverride).HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<NamespaceAlias>(e =>
        {
            e.ToTable("namespace_aliases");
            e.HasIndex(x => x.OldSlug);
            e.Property(x => x.OldSlug).HasMaxLength(100);
            e.Property(x => x.NewSlug).HasMaxLength(100);
        });
        b.Entity<FeedSyncReport>(e => { e.ToTable("feed_sync_reports"); e.HasIndex(x => new { x.FeedId, x.StartedAt }); });
        b.Entity<PluginComponent>(e =>
        {
            e.ToTable("plugin_components");
            e.HasIndex(x => x.PluginVersionId);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Slug).HasMaxLength(100);
            e.Property(x => x.ExactVersion).HasMaxLength(64);
        });
        b.Entity<Governance>(e =>
        {
            e.ToTable("governance");
            e.HasKey(x => x.ArtifactId);
            e.Property(x => x.OwnerId).HasMaxLength(200);
            e.Property(x => x.Risk).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DataClass).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Capabilities).HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                    (a, c) => a!.SequenceEqual(c!), c => c.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())), c => c.ToList()));
        });
        b.Entity<AuditEntry>(e => { e.ToTable("audit_log"); e.HasIndex(x => x.At); });
    }
}
