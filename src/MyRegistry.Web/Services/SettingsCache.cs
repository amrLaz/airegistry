using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Services;

/// <summary>
/// Cache court des réglages pour le layout (titre). Utilise son propre scope : un DbContext partagé avec la page
/// provoquerait des opérations concurrentes dans le circuit Blazor.
/// </summary>
public sealed class SettingsCache(IServiceScopeFactory scopes)
{
    private Dictionary<string, string>? _values;
    private DateTime _loadedAt;

    public async Task<Dictionary<string, string>> GetAsync()
    {
        if (_values is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30)) return _values;
        using var scope = scopes.CreateScope();
        _values = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAllAsync();
        _loadedAt = DateTime.UtcNow;
        return _values;
    }

    public async Task<string> TitleAsync() => (await GetAsync()).GetValueOrDefault(SettingKeys.Title) ?? "MyRegistry";
    public async Task<string> DefaultNamespaceAsync() => (await GetAsync()).GetValueOrDefault(SettingKeys.DefaultNamespace) ?? "myregistry";
    public void Invalidate() => _values = null;
}
