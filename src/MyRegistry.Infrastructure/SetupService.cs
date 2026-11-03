using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record SetupStatus(bool Completed, bool Needed, string Title, string DefaultNamespace, string ContactEmail, string PublicUrl, int Artifacts, int Feeds);

public class SetupService(AppDbContext db, SettingsService settings)
{
    public async Task<SetupStatus> StatusAsync(Role role, CancellationToken ct = default)
    {
        var s = await settings.GetAllAsync(ct);
        var artifacts = await db.Artifacts.CountAsync(ct);
        return new(s.GetValueOrDefault(SettingKeys.SetupCompleted) == "true", SetupRules.NeedsSetup(s.GetValueOrDefault(SettingKeys.SetupCompleted), artifacts, role),
            s.GetValueOrDefault(SettingKeys.Title, ""), s.GetValueOrDefault(SettingKeys.DefaultNamespace, ""), s.GetValueOrDefault(SettingKeys.ContactEmail, ""),
            s.GetValueOrDefault(SettingKeys.PublicUrl, ""), artifacts, await db.Feeds.CountAsync(ct));
    }

    public Task<IReadOnlyList<Issue>> ApplyIdentityAsync(string title, string ns, string? contactEmail, string? publicUrl, string actor, CancellationToken ct = default) =>
        settings.UpdateAsync(new Dictionary<string, string>
        {
            [SettingKeys.Title] = title, [SettingKeys.DefaultNamespace] = ns,
            [SettingKeys.ContactEmail] = contactEmail ?? "", [SettingKeys.PublicUrl] = publicUrl ?? "",
        }, actor, ct);

    public async Task CompleteAsync(string actor, CancellationToken ct = default)
    {
        await settings.SetInternalAsync(new Dictionary<string, string> { [SettingKeys.SetupCompleted] = "true" }, ct);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "setup-complete", Entity = "Settings", EntityId = "global" });
        await db.SaveChangesAsync(ct);
    }
}
