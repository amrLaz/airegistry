using MyRegistry.Application.Rules;

namespace MyRegistry.Domain.Tests.Rules;

public class SecretScannerTests
{
    private static IReadOnlyList<SecretFinding> Scan(string content, string path = "a.md") =>
        SecretScanner.Scan([(path, content)]);

    [Fact]
    public void Private_key_is_blocking_with_file_and_line()
    {
        var f = Assert.Single(Scan("ok\n-----BEGIN RSA PRIVATE KEY-----\nabc"));
        Assert.Equal("private-key", f.Detector);
        Assert.Equal(2, f.Line);
        Assert.Equal("a.md", f.File);
        Assert.True(f.Blocking);
    }

    [Fact]
    public void Github_token_is_detected() =>
        Assert.Contains(Scan("t=ghp_" + new string('a', 36)), f => f.Detector == "github-token" && f.Blocking);

    [Fact]
    public void Aws_key_is_detected() =>
        Assert.Contains(Scan("AKIAIOSFODNN7EXAMPLQ"), f => f.Detector == "aws-access-key");

    [Fact]
    public void Assigned_password_is_detected() =>
        Assert.Contains(Scan("password = \"hunter2hunter2\""), f => f.Detector == "password-assignment" && f.Blocking);

    [Fact]
    public void Assigned_api_key_is_detected() =>
        Assert.Contains(Scan("API_KEY=abcd1234efgh5678ijkl"), f => f.Detector == "api-key-assignment");

    [Fact]
    public void Placeholders_are_not_secrets() =>
        Assert.Empty(Scan("api_key: your-api-key-goes-here-xxxx"));

    [Fact]
    public void Loopback_url_is_blocking() =>
        Assert.Contains(Scan("http://localhost:8080/x"), f => f.Detector == "local-loopback-url" && f.Blocking);

    [Theory]
    [InlineData("http://192.168.1.10/api", "private-network-url")]
    [InlineData("http://10.0.0.5", "private-network-url")]
    [InlineData("C:\\Users\\bob\\x", "machine-path")]
    [InlineData("/home/bob/project", "machine-path")]
    [InlineData("https://abc.ngrok.io", "temporary-tunnel")]
    [InlineData("see ~/.aws/credentials", "credentials-file-ref")]
    [InlineData("https://api-staging.corp.com", "staging-endpoint")]
    public void Observation_detectors_do_not_block(string content, string detector)
    {
        var f = Assert.Single(Scan(content), x => x.Detector == detector);
        Assert.False(f.Blocking);
    }

    [Fact]
    public void Clean_content_has_no_findings() => Assert.Empty(Scan("# Hello\nNothing here."));
}
