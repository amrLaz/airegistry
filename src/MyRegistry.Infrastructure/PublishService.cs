using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public class PublishService(AppDbContext db, PullRequestWorkflow pullRequests)
{
    public async Task<ImportResult> ImportAsync(ImportRequest req, CurrentUser user, CancellationToken ct = default)
    {
        List<FilePayload> files;
        try { files = PayloadBuilder.Build(req.FileName, req.RawContent, req.AdditionalFiles, req.BinaryFiles); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { return Fail([new("invalid-payload", ex.Message)]); }

        var existing = await db.Artifacts.Include(a => a.Versions).Include(a => a.Tags)
            .FirstOrDefaultAsync(a => a.Namespace == req.Namespace && a.Name == req.Name && a.Type == req.Type, ct);

        var nameCheck = NameRules.Check(req.Namespace, req.Name, req.Type, creation: existing is null);
        if (!nameCheck.IsValid) return Fail(nameCheck.Errors, canonical: nameCheck.Canonical);

        var report = Validation.Run(req.Type, files);
        if (!report.IsValid) return Fail(report.Errors, report.Warnings);

        var notes = new List<string>();
        if (existing is null)
        {
            var sameType = await db.Artifacts.Where(a => a.Type == req.Type).Select(a => new { a.Namespace, a.Name }).ToListAsync(ct);
            if (sameType.Any(a => a.Namespace == req.Namespace && NameSimilarity.AreClose(a.Name, req.Name)))
                return Fail([new("name-too-close", "Nom trop proche d'un artefact existant du même namespace.")]);
            if (sameType.Any(a => a.Namespace != req.Namespace && NameSimilarity.AreClose(a.Name, req.Name)))
                notes.Add("name-confusion");
        }

        var decision = VersionRules.Resolve(req.Version, existing?.Versions.Select(v => v.Version) ?? []);
        if (decision.Error is not null) return Fail([new("version-duplicate", decision.Error)]);

        // Publication par pull request : n'aboutit que si la PR est créée (un échec Git annule la publication).
        var versionId = Guid.NewGuid();
        PullRequestInfo? pr = null;
        if (await pullRequests.SystemTargetAsync(ct) is { } target)
        {
            try { pr = await pullRequests.OpenAsync(target, req.Namespace, req.Name, decision.Version, versionId, files, user.Email, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or ArgumentException)
            { return Fail([new("pull-request", $"La pull request de gouvernance n'a pas pu être créée : {ex.Message}")], report.Warnings); }
        }

        var artifact = existing ?? new Artifact { Namespace = req.Namespace, Name = req.Name, Type = req.Type };
        if (existing is null) db.Artifacts.Add(artifact);

        var version = new ArtifactVersion
        {
            Id = versionId, Artifact = artifact, Version = decision.Version, Description = req.Description,
            PrId = pr?.Id, PrUrl = pr?.Url, PrState = pr is null ? null : "Open",
            ContentHash = ContentHasher.Hash(files), QualityGrade = report.Grade, MainFile = PayloadBuilder.Normalize(req.FileName),
            CreatedBy = user.Email, Status = VersionStatus.Pending,
            Files = files.Select(f => new VersionFile { Path = f.Path, Content = f.Content, IsBinary = f.IsBinary }).ToList(),
        };
        version.History.Add(new StatusHistory { To = VersionStatus.Pending, UserId = user.Email, Note = notes.Count > 0 ? string.Join(",", notes) : "import" });
        db.Versions.Add(version);

        foreach (var name in (req.Tags ?? []).Select(NameRules.Canonicalize).Where(n => n.Length > 0).Distinct())
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Type == req.Type && t.Name == name, ct)
                      ?? db.Tags.Local.FirstOrDefault(t => t.Type == req.Type && t.Name == name)
                      ?? db.Tags.Add(new Tag { Type = req.Type, Name = name }).Entity;
            if (!artifact.Tags.Contains(tag)) artifact.Tags.Add(tag);
        }

        db.Audit.Add(new AuditEntry { UserId = user.Email, Action = "import", Entity = "ArtifactVersion", EntityId = version.Id.ToString(), Details = $"{req.Namespace}/{req.Name}@{version.Version}" });
        await db.SaveChangesAsync(ct);

        return new(true, version.Id, version.Version, decision.AutoBumped, version.QualityGrade, version.ContentHash,
            null, [], report.Warnings, notes, pr?.Url);
    }

    private static ImportResult Fail(IReadOnlyList<Issue> errors, IReadOnlyList<Issue>? warnings = null, string? canonical = null) =>
        new(false, null, null, false, null, null, canonical, errors, warnings ?? [], []);
}
