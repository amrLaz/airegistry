using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class RetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Fam = Guid.NewGuid();

    private static RetentionCandidate C(string version, VersionStatus status, int ageDays, Guid? family = null, bool pinned = false, bool pr = false, bool reports = false) =>
        new(Guid.NewGuid(), family ?? Fam, version, status, Now.AddDays(-ageDays), pinned, pr, reports);

    private static string[] Pruned(IEnumerable<RetentionCandidate> cs, int keep, int age = 30) =>
        RetentionPolicy.Select(cs, keep, age, Now).Select(d => d.Version).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [Fact] public void Disabled_by_default_nothing_is_ever_selected() =>
        Assert.Empty(Pruned([C("1.0.0", VersionStatus.Deprecated, 999), C("2.0.0", VersionStatus.Approved, 1)], keep: 0));

    [Fact] public void Only_old_deprecated_versions_beyond_the_newest_n_are_selected()
    {
        var cs = new[]
        {
            C("1.0.0", VersionStatus.Deprecated, 400), C("1.1.0", VersionStatus.Deprecated, 300), C("1.2.0", VersionStatus.Deprecated, 200),
            C("1.3.0", VersionStatus.Deprecated, 100), C("2.0.0", VersionStatus.Approved, 10),
        };
        Assert.Equal(["1.0.0", "1.1.0"], Pruned(cs, keep: 3));   // 2.0.0, 1.3.0 et 1.2.0 sont les 3 plus récentes
        Assert.Equal(["1.0.0", "1.1.0", "1.2.0", "1.3.0"], Pruned(cs, keep: 1));
    }

    [Theory]
    [InlineData(VersionStatus.Approved)] [InlineData(VersionStatus.Pending)] [InlineData(VersionStatus.ToReview)]
    public void Live_versions_are_never_selected_whatever_their_age(VersionStatus status) =>
        Assert.Empty(Pruned([C("1.0.0", status, 2000), C("2.0.0", VersionStatus.Approved, 1)], keep: 1));

    [Fact] public void The_highest_semver_is_kept_even_when_deprecated_and_old()
    {
        var cs = new[] { C("3.0.0", VersionStatus.Deprecated, 900), C("1.0.0", VersionStatus.Deprecated, 800), C("2.0.0", VersionStatus.Deprecated, 700) };
        // 3.0.0 est la plus haute version (gardée même si ancienne) ; 2.0.0 est la plus récente (gardée au titre des N plus récentes).
        Assert.Equal(["1.0.0"], Pruned(cs, keep: 1));
    }

    [Fact] public void Versions_pinned_by_a_plugin_or_with_open_pr_or_reports_are_kept()
    {
        var cs = new[]
        {
            C("1.0.0", VersionStatus.Deprecated, 500, pinned: true), C("1.1.0", VersionStatus.Deprecated, 500, pr: true),
            C("1.2.0", VersionStatus.Deprecated, 500, reports: true), C("1.3.0", VersionStatus.Deprecated, 500), C("2.0.0", VersionStatus.Approved, 1),
        };
        Assert.Equal(["1.3.0"], Pruned(cs, keep: 1));
    }

    [Fact] public void Recent_deprecations_are_kept_and_the_age_floor_is_seven_days()
    {
        var cs = new[] { C("1.0.0", VersionStatus.Deprecated, 20), C("1.1.0", VersionStatus.Deprecated, 5), C("2.0.0", VersionStatus.Approved, 1) };
        Assert.Empty(Pruned(cs, keep: 1, age: 30));
        Assert.Equal(["1.0.0"], Pruned(cs, keep: 1, age: 0));    // plancher : 7 jours même si on demande 0
        Assert.Equal(["1.0.0"], Pruned(cs, keep: 1, age: 7));
    }

    [Fact] public void Families_are_independent_and_each_keeps_its_own_latest_and_newest()
    {
        var other = Guid.NewGuid();
        var cs = new[]
        {
            C("1.0.0", VersionStatus.Deprecated, 500), C("2.0.0", VersionStatus.Deprecated, 400),
            C("5.0.0", VersionStatus.Deprecated, 500, other), C("6.0.0", VersionStatus.Deprecated, 100, other),
        };
        Assert.Equal(["1.0.0", "5.0.0"], Pruned(cs, keep: 1));
    }

    [Fact] public void Decisions_explain_themselves()
    {
        var d = RetentionPolicy.Select([C("1.0.0", VersionStatus.Deprecated, 400), C("2.0.0", VersionStatus.Approved, 1)], 1, 30, Now).Single();
        Assert.Contains("dépréciée", d.Reason); Assert.Contains("400 jours", d.Reason);
    }

    [Theory]
    [InlineData("retention_keep_versions", "0", true)] [InlineData("retention_keep_versions", "10", true)] [InlineData("retention_keep_versions", "-1", false)]
    [InlineData("retention_keep_versions", "1001", false)] [InlineData("retention_keep_versions", "abc", false)]
    [InlineData("retention_min_age_days", "90", true)] [InlineData("retention_min_age_days", "6", false)] [InlineData("retention_min_age_days", "99999", false)]
    public void Retention_settings_are_validated(string key, string value, bool ok) =>
        Assert.Equal(ok, SettingsRules.Validate(new Dictionary<string, string> { [key] = value }).Count == 0);
}
