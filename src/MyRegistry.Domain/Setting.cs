namespace MyRegistry.Domain;

/// <summary>Paramètre clé/valeur (titre, namespace par défaut, URL publique...). Jamais en dur dans le code.</summary>
public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
