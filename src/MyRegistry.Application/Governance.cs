using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

/// <summary>Annuaire des utilisateurs (rôles stockés en base, attribués par un Admin).</summary>
public interface IUserDirectory
{
    /// <summary>Retourne l'utilisateur ; le crée avec <paramref name="defaultRole"/> s'il n'existe pas.</summary>
    (Role Role, bool Active, string DisplayName) Resolve(string email, Role defaultRole);
}

public sealed record VersionRef(Guid Id, string Version, DateTime CreatedAt);

public enum DeletionVerdict { Allowed, NotFound, OnlyVersion, LatestVersion }

/// <summary>« La dernière version est protégée ; la seule version restante ne peut pas être supprimée » (docs/05).</summary>
public static class DeletionPolicy
{
    public static DeletionVerdict Check(Guid versionId, IReadOnlyCollection<VersionRef> family)
    {
        if (family.All(v => v.Id != versionId)) return DeletionVerdict.NotFound;
        if (family.Count == 1) return DeletionVerdict.OnlyVersion;
        return Latest(family).Id == versionId ? DeletionVerdict.LatestVersion : DeletionVerdict.Allowed;
    }

    /// <summary>Plus haute version SemVer ; à défaut (versions non SemVer), la plus récente.</summary>
    public static VersionRef Latest(IReadOnlyCollection<VersionRef> family)
    {
        var semver = family.Where(v => VersionRules.IsSemVer(v.Version)).ToList();
        return semver.Count > 0
            ? semver.OrderBy(v => v.Version, Comparer<string>.Create(VersionRules.Compare)).ThenBy(v => v.CreatedAt).Last()
            : family.OrderBy(v => v.CreatedAt).Last();
    }
}
