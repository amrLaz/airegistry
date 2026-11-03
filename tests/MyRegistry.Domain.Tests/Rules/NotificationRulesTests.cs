using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class NotificationRulesTests
{
    [Fact] public void Everything_is_enabled_by_default() =>
        Assert.All(Enum.GetValues<NotificationKind>(), k => Assert.True(NotificationRules.IsEnabled(new Dictionary<NotificationKind, bool>(), k)));

    [Fact] public void Only_an_explicit_false_disables_a_kind()
    {
        var prefs = new Dictionary<NotificationKind, bool> { [NotificationKind.CommentAdded] = false, [NotificationKind.StatusChanged] = true };
        Assert.False(NotificationRules.IsEnabled(prefs, NotificationKind.CommentAdded));
        Assert.True(NotificationRules.IsEnabled(prefs, NotificationKind.StatusChanged));
        Assert.True(NotificationRules.IsEnabled(prefs, NotificationKind.ReportFiled));
    }

    [Fact] public void Recipients_exclude_the_actor_blanks_and_duplicates()
    {
        var r = NotificationRules.Recipients(["a@x.com", "A@X.com", "b@x.com", "", null, "  ", "me@x.com"], "ME@x.com");
        Assert.Equal(["a@x.com", "b@x.com"], r);
    }

    [Fact] public void Subject_and_link_formats()
    {
        Assert.Equal("ns/name@1.2.3", NotificationRules.Subject("ns", "name", "1.2.3"));
        var id = Guid.NewGuid();
        Assert.Equal($"/artifact/{id}", NotificationRules.ArtifactLink(id));
    }
}
