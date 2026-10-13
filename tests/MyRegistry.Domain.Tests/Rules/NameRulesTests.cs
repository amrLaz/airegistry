using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class NameRulesTests
{
    [Theory]
    [InlineData("myregistry", "pdf-extractor")]
    [InlineData("ab", "x1")]
    public void Valid_names_pass(string ns, string name) =>
        Assert.True(NameRules.Check(ns, name, ArtifactType.Skill, creation: true).IsValid);

    [Theory]
    [InlineData("a", "pdf-extractor", "ns-length")]
    [InlineData("myregistry", "x", "name-length")]
    [InlineData("myregistry", "Pdf_Extractor", "name-format")]
    [InlineData("myregistry", "-pdf", "name-format")]
    [InlineData("myregistry", "pdf--tool", "name-format")]
    public void Invalid_names_are_rejected(string ns, string name, string code)
    {
        var r = NameRules.Check(ns, name, ArtifactType.Skill, true);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Code == code);
    }

    [Fact]
    public void Namespace_length_over_32_is_rejected() =>
        Assert.Contains(NameRules.Check(new string('a', 33), "tool", ArtifactType.Skill, true).Errors, e => e.Code == "ns-length");

    [Fact]
    public void Name_length_over_48_is_rejected() =>
        Assert.Contains(NameRules.Check("myregistry", new string('a', 49), ArtifactType.Skill, true).Errors, e => e.Code == "name-length");

    [Fact]
    public void Combined_form_over_64_is_rejected()
    {
        var r = NameRules.Check(new string('a', 30), new string('b', 40), ArtifactType.Skill, true);
        Assert.Contains(r.Errors, e => e.Code == "combined-length");
    }

    [Theory]
    [InlineData(ArtifactType.Skill, "synced")]
    [InlineData(ArtifactType.Agent, "default")]
    [InlineData(ArtifactType.Agent, "worker")]
    [InlineData(ArtifactType.Agent, "explorer")]
    public void Reserved_names_are_rejected(ArtifactType type, string name) =>
        Assert.Contains(NameRules.Check("myregistry", name, type, true).Errors, e => e.Code == "reserved-name");

    [Fact]
    public void Reserved_agent_name_is_fine_for_a_prompt() =>
        Assert.True(NameRules.Check("myregistry", "worker", ArtifactType.Prompt, true).IsValid);

    [Theory]
    [InlineData("tool-gh")]
    [InlineData("tool-azd")]
    [InlineData("tool-feed")]
    public void Forbidden_suffixes_are_rejected(string name) =>
        Assert.Contains(NameRules.Check("myregistry", name, ArtifactType.Skill, true).Errors, e => e.Code == "forbidden-suffix");

    [Theory]
    [InlineData(ArtifactType.Skill, "my-skill")]
    [InlineData(ArtifactType.Agent, "agent-review")]
    [InlineData(ArtifactType.Hook, "git-hook")]
    public void Name_containing_its_type_is_rejected_on_creation(ArtifactType type, string name) =>
        Assert.Contains(NameRules.Check("myregistry", name, type, true).Errors, e => e.Code == "name-contains-type");

    [Fact]
    public void Name_containing_type_is_tolerated_when_not_creating() =>
        Assert.True(NameRules.Check("myregistry", "my-skill", ArtifactType.Skill, creation: false).IsValid);

    [Fact]
    public void Non_standard_name_gets_a_canonical_proposal()
    {
        var r = NameRules.Check("myregistry", "Pdf Extractor_v2", ArtifactType.Skill, true);
        Assert.False(r.IsValid);
        Assert.Equal("pdf-extractor-v2", r.Canonical);
    }
}

public class NameSimilarityTests
{
    [Theory]
    [InlineData("pdf-extractor", "pdf-extractr")]   // suppression
    [InlineData("pdf-extractor", "pdf-extractors")] // insertion
    [InlineData("pdf-extractor", "pdf-extractar")]  // substitution
    [InlineData("pdf-extractor", "pdf-extarctor")]  // transposition
    [InlineData("hello-world", "he11o-world")]      // caractères ambigus
    [InlineData("tool0", "toolo")]
    public void Close_names_are_detected(string a, string b) => Assert.True(NameSimilarity.AreClose(a, b));

    [Theory]
    [InlineData("pdf-extractor", "pdf-extractor")] // identique : c'est une nouvelle version, pas un doublon proche
    [InlineData("pdf-extractor", "csv-parser")]
    [InlineData("abc", "xyz")]
    public void Distant_or_identical_names_are_not_close(string a, string b) => Assert.False(NameSimilarity.AreClose(a, b));
}
