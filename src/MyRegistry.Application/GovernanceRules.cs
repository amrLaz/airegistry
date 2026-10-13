using System.Text.RegularExpressions;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record GovernanceInput(string? OwnerId, RiskLevel Risk, DataClass DataClass, int? ReviewEveryDays, string[]? Capabilities, bool MarkReviewed = false);

public static class GovernancePolicy
{
    public const int MaxCapabilities = 20;

    /// <summary>Modifiable par l'auteur, un SecurityValidator ou un Admin (docs/01).</summary>
    public static bool CanEdit(CurrentUser user, string authorEmail) =>
        user.IsAuthenticated && (user.Role is Role.SecurityValidator or Role.Admin
            || string.Equals(user.Email, authorEmail, StringComparison.OrdinalIgnoreCase));

    /// <summary>Une revue est due si une fréquence est définie et que la dernière revue (ou, à défaut, la création du contrat) est trop ancienne.</summary>
    public static bool ReviewDue(int? everyDays, DateTime? lastReviewed, DateTime createdAt, DateTime now) =>
        everyDays is > 0 && now - (lastReviewed ?? createdAt) > TimeSpan.FromDays(everyDays.Value);

    public static string NormalizeCapability(string c) => NameRules.Canonicalize(c);

    public static (IReadOnlyList<string> Capabilities, IReadOnlyList<Issue> Errors) Validate(GovernanceInput i)
    {
        var errors = new List<Issue>();
        if (string.IsNullOrWhiteSpace(i.OwnerId) || !i.OwnerId.Contains('@')) errors.Add(new("owner", "Un propriétaire (adresse e-mail) est obligatoire."));
        if (i.ReviewEveryDays is < 1 or > 730) errors.Add(new("review-every-days", "La fréquence de revue doit être comprise entre 1 et 730 jours."));
        var caps = (i.Capabilities ?? []).Select(NormalizeCapability).Where(c => c.Length > 0).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (caps.Count > MaxCapabilities) errors.Add(new("capabilities", $"Au plus {MaxCapabilities} capabilities."));
        if (caps.Any(c => c.Length > 40)) errors.Add(new("capabilities", "Une capability ne dépasse pas 40 caractères."));
        return (caps, errors);
    }
}

public static class SettingsRules
{
    private static readonly Regex Ns = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>Valide un lot de réglages ; ne retourne que les erreurs.</summary>
    public static IReadOnlyList<Issue> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<Issue>();
        foreach (var (k, v) in values)
        {
            switch (k)
            {
                case SettingKeys.Title when string.IsNullOrWhiteSpace(v) || v.Trim().Length > 80:
                    errors.Add(new(k, "Le titre est obligatoire (80 caractères maximum).")); break;
                case SettingKeys.DefaultNamespace when v.Length is < 2 or > 32 || !Ns.IsMatch(v):
                    errors.Add(new(k, "Namespace : 2 à 32 caractères, minuscules, chiffres, tirets simples.")); break;
                case SettingKeys.PublicUrl when v.Length > 0 && !(Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"):
                    errors.Add(new(k, "URL publique invalide (http ou https).")); break;
                case not (SettingKeys.Title or SettingKeys.DefaultNamespace or SettingKeys.PublicUrl):
                    errors.Add(new(k, "Réglage inconnu.")); break;
            }
        }
        return errors;
    }
}
