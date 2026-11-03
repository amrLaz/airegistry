using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class CommentPolicyTests
{
    [Theory]
    [InlineData("a@x.com", "a@x.com", Role.Reader, true)]
    [InlineData("a@x.com", "A@X.com", Role.Reader, true)]
    [InlineData("a@x.com", "b@x.com", Role.Reader, false)]
    [InlineData("a@x.com", "b@x.com", Role.Publisher, false)]
    [InlineData("a@x.com", "b@x.com", Role.Moderator, true)]
    [InlineData("a@x.com", "b@x.com", Role.Admin, true)]
    public void Delete_is_for_the_author_or_a_moderator(string author, string user, Role role, bool expected) =>
        Assert.Equal(expected, CommentPolicy.CanDelete(author, user, role));

    [Fact] public void Three_distinct_reporters_hide_a_comment() =>
        Assert.True(CommentPolicy.ShouldAutoHide(["a@x.com", "b@x.com", "c@x.com"]));

    [Fact] public void Repeated_reports_from_the_same_user_do_not_count() =>
        Assert.False(CommentPolicy.ShouldAutoHide(["a@x.com", "A@x.com", " a@x.com ", "b@x.com"]));

    [Fact] public void Own_comment_cannot_be_reported()
    {
        Assert.False(CommentPolicy.CanReport("a@x.com", "A@X.com"));
        Assert.True(CommentPolicy.CanReport("a@x.com", "b@x.com"));
    }
}
