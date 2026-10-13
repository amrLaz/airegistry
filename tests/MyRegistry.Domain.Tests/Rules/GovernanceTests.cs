using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class GovernancePolicyTests
{
    private static CurrentUser U(string email, Role role, bool auth = true) => new(email, email, role, auth);

    [Theory]
    [InlineData(Role.Admin, "x@x.io", true)]
    [InlineData(Role.SecurityValidator, "x@x.io", true)]
    [InlineData(Role.Moderator, "x@x.io", false)]
    [InlineData(Role.Publisher, "author@x.io", true)]   // l'auteur
    [InlineData(Role.Publisher, "other@x.io", false)]
    [InlineData(Role.Reader, "author@x.io", true)]
    public void Who_can_edit(Role role, string email, bool expected) =>
        Assert.Equal(expected, GovernancePolicy.CanEdit(U(email, role), "AUTHOR@x.io"));

    [Fact] public void Inactive_user_cannot_edit() =>
        Assert.False(GovernancePolicy.CanEdit(U("author@x.io", Role.Admin, auth: false), "author@x.io"));

    private static readonly DateTime Now = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact] public void No_frequency_means_never_due() => Assert.False(GovernancePolicy.ReviewDue(null, null, Now.AddYears(-3), Now));
    [Fact] public void Due_when_last_review_is_too_old() => Assert.True(GovernancePolicy.ReviewDue(90, Now.AddDays(-91), Now.AddYears(-1), Now));
    [Fact] public void Not_due_when_recently_reviewed() => Assert.False(GovernancePolicy.ReviewDue(90, Now.AddDays(-10), Now.AddYears(-1), Now));
    [Fact] public void Falls_back_to_creation_date() => Assert.True(GovernancePolicy.ReviewDue(30, null, Now.AddDays(-31), Now));
    [Fact] public void Exactly_on_the_day_is_not_yet_due() => Assert.False(GovernancePolicy.ReviewDue(30, Now.AddDays(-30), Now.AddYears(-1), Now));

    [Fact] public void Valid_input_normalizes_and_dedupes_capabilities()
    {
        var (caps, errors) = GovernancePolicy.Validate(new("o@x.io", RiskLevel.High, DataClass.Internal, 90, ["Network Access", "network-access", "File Write"]));
        Assert.Empty(errors);
        Assert.Equal(["file-write", "network-access"], caps);
    }

    [Theory]
    [InlineData(null, "owner")]
    [InlineData("", "owner")]
    [InlineData("pas-un-mail", "owner")]
    public void Owner_is_required(string? owner, string code) =>
        Assert.Contains(GovernancePolicy.Validate(new(owner, RiskLevel.Low, DataClass.Public, null, null)).Errors, e => e.Code == code);

    [Theory]
    [InlineData(0)]
    [InlineData(731)]
    public void Review_frequency_bounds(int days) =>
        Assert.Contains(GovernancePolicy.Validate(new("o@x.io", RiskLevel.Low, DataClass.Public, days, null)).Errors, e => e.Code == "review-every-days");

    [Fact] public void Too_many_capabilities() =>
        Assert.Contains(GovernancePolicy.Validate(new("o@x.io", RiskLevel.Low, DataClass.Public, null,
            Enumerable.Range(0, 21).Select(i => $"cap{i}").ToArray())).Errors, e => e.Code == "capabilities");
}

public class SettingsRulesTests
{
    private static IReadOnlyList<MyRegistry.Application.Rules.Issue> V(string k, string v) =>
        SettingsRules.Validate(new Dictionary<string, string> { [k] = v });

    [Fact] public void Valid_values_pass()
    {
        Assert.Empty(V(SettingKeys.Title, "Mon Registre"));
        Assert.Empty(V(SettingKeys.DefaultNamespace, "acme-ai"));
        Assert.Empty(V(SettingKeys.PublicUrl, "https://registry.acme.io"));
        Assert.Empty(V(SettingKeys.PublicUrl, ""));
    }

    [Theory]
    [InlineData(SettingKeys.Title, "")]
    [InlineData(SettingKeys.Title, "   ")]
    [InlineData(SettingKeys.DefaultNamespace, "A")]
    [InlineData(SettingKeys.DefaultNamespace, "Bad_NS")]
    [InlineData(SettingKeys.DefaultNamespace, "-x-")]
    [InlineData(SettingKeys.PublicUrl, "not a url")]
    [InlineData(SettingKeys.PublicUrl, "ftp://x.io")]
    [InlineData("inconnu", "x")]
    public void Invalid_values_are_rejected(string key, string value) => Assert.NotEmpty(V(key, value));

    [Theory]
    [InlineData("")]
    [InlineData("file:///var/lib/myregistry/marketplace")]
    [InlineData("/var/lib/myregistry/marketplace")]
    public void Marketplace_repo_accepts_local_absolute_locations(string v) => Assert.Empty(V(SettingKeys.MarketplaceRepo, v));

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("/var/../etc")]
    [InlineData("file://remote-host/share")]
    public void Marketplace_repo_rejects_remote_relative_root_and_traversal(string v) => Assert.NotEmpty(V(SettingKeys.MarketplaceRepo, v));

    [Fact] public void Title_length_limit() => Assert.NotEmpty(V(SettingKeys.Title, new string('a', 81)));
}

public class RepoLocationTests
{
    [Fact] public void File_url_and_plain_path_resolve_to_the_same_local_path() =>
        Assert.Equal(RepoLocation.ToLocalPath("/srv/repo"), RepoLocation.ToLocalPath("file:///srv/repo"));

    [Fact] public void Trailing_slash_is_ignored() => Assert.Equal("/srv/repo", RepoLocation.ToLocalPath("/srv/repo/"));
    [Fact] public void Escaped_characters_are_decoded() => Assert.Equal("/srv/my repo", RepoLocation.ToLocalPath("file:///srv/my%20repo"));
    [Fact] public void File_url_round_trip() => Assert.Equal("file:///srv/repo", RepoLocation.ToFileUrl("/srv/repo"));
}

public class MarketplaceSnapshotTests
{
    [Fact] public void Writes_the_same_manifest_for_the_four_agents()
    {
        var files = MarketplaceSnapshot.Files("{}");
        Assert.Equal(4, files.Count);
        Assert.Contains(".github/plugin/marketplace.json", files.Keys);
        Assert.Contains(".claude-plugin/marketplace.json", files.Keys);
        Assert.Contains(".agents/plugins/marketplace.json", files.Keys);
        Assert.Contains(".cursor-plugin/marketplace.json", files.Keys);
        Assert.All(files.Values, v => Assert.Equal("{}", v));
    }

    [Fact] public void Digest_is_stable_and_content_sensitive()
    {
        Assert.Equal(MarketplaceSnapshot.Digest("a"), MarketplaceSnapshot.Digest("a"));
        Assert.NotEqual(MarketplaceSnapshot.Digest("a"), MarketplaceSnapshot.Digest("b"));
        Assert.Equal(64, MarketplaceSnapshot.Digest("a").Length);
    }
}
