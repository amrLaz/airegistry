using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class SetupRulesTests
{
    [Theory]
    [InlineData(null, 0, Role.Admin, true)]
    [InlineData("false", 0, Role.Admin, true)]
    [InlineData("true", 0, Role.Admin, false)]
    [InlineData("TRUE", 0, Role.Admin, false)]
    [InlineData(null, 3, Role.Admin, false)]
    [InlineData(null, 0, Role.Moderator, false)]
    [InlineData(null, 0, Role.Publisher, false)]
    public void Setup_is_offered_only_to_an_admin_on_a_blank_instance(string? done, int artifacts, Role role, bool expected) =>
        Assert.Equal(expected, SetupRules.NeedsSetup(done, artifacts, role));

    [Fact] public void Marketplace_snippets_use_the_public_url_without_double_slashes()
    {
        Assert.Equal("https://r.example.com/marketplace.json", SetupRules.MarketplaceUrl("https://r.example.com/"));
        var (claude, vscode) = SetupRules.MarketplaceSnippets("https://r.example.com");
        Assert.Equal("claude plugin marketplace add https://r.example.com/marketplace.json", claude);
        Assert.Equal("\"chat.plugins.marketplaces\": [\"https://r.example.com/marketplace.json\"]", vscode);
    }

    [Theory]
    [InlineData("a@b.co", true)] [InlineData("", true)] [InlineData("pas un mail", false)] [InlineData("a@b", false)]
    public void Contact_email_is_validated_when_present(string email, bool ok) =>
        Assert.Equal(ok, SettingsRules.Validate(new Dictionary<string, string> { [SettingKeys.ContactEmail] = email }).Count == 0);

    [Fact] public void The_completion_flag_is_not_editable_by_users() =>
        Assert.Contains(SettingsRules.Validate(new Dictionary<string, string> { [SettingKeys.SetupCompleted] = "true" }), e => e.Message == "Réglage inconnu.");
}
