using System.Text.RegularExpressions;

namespace MyRegistry.Application.Rules;

public sealed record SecretFinding(string Detector, string File, int Line, bool Blocking);

public static class SecretScanner
{
    private sealed record Detector(string Name, Regex Pattern, bool Blocking, bool SkipPlaceholders = false);

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Detector[] Detectors =
    [
        new("private-key", R(@"-----BEGIN [A-Z ]*PRIVATE KEY-----"), true),
        new("github-token", new Regex(@"gh[pousr]_[A-Za-z0-9]{36}", RegexOptions.Compiled), true),
        new("aws-access-key", new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), true),
        new("password-assignment", R(@"password\s*[:=]\s*[""']?[^\s""']{6,}"), true, true),
        new("api-key-assignment", R(@"(api[_-]?key|secret|token)\s*[:=]\s*[""']?[A-Za-z0-9_\-]{16,}"), true, true),
        new("local-loopback-url", R(@"https?://(localhost|127\.0\.0\.1|\[::1\])"), true),
        new("private-network-url", R(@"https?://(10\.\d+\.\d+\.\d+|192\.168\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.\d+\.\d+)"), false),
        new("machine-path", R(@"([A-Za-z]:\\Users\\|/home/[a-z0-9_-]+|/Users/[A-Za-z0-9_-]+)"), false),
        new("temporary-tunnel", R(@"(ngrok|trycloudflare\.com|loca\.lt|serveo\.net)"), false),
        new("dev-port", R(@":(3000|5173|8000|8080)\b"), false),
        new("staging-endpoint", R(@"https?://[^\s""']*(staging|recette|uat)[^\s""']*"), false),
        new("credentials-file-ref", R(@"(\.aws/credentials|id_rsa|\.npmrc|\.netrc)"), false),
    ];

    private static readonly Regex Placeholder = new("(your|example|xxx|placeholder|changeme|<)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<SecretFinding> Scan(IEnumerable<(string Path, string Content)> files)
    {
        var result = new List<SecretFinding>();
        foreach (var (path, content) in files)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                foreach (var d in Detectors)
                {
                    var m = d.Pattern.Match(lines[i]);
                    if (!m.Success) continue;
                    if (d.SkipPlaceholders && Placeholder.IsMatch(m.Value)) continue;
                    // dev-port ne s'applique que s'il n'est pas déjà couvert par la détection de loopback
                    result.Add(new(d.Name, path, i + 1, d.Blocking));
                }
        }
        return result;
    }
}
