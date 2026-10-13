namespace MyRegistry.Web.Services;

/// <summary>Traductions FR/EN minimales ; langue modifiable à l'exécution.</summary>
public sealed class Localizer
{
    private static readonly Dictionary<string, (string Fr, string En)> Texts = new()
    {
        ["welcome"] = ("Bienvenue sur", "Welcome to"),
        ["tagline"] = ("Le registre interne des artefacts IA : serveurs MCP, skills, agents, prompts, plugins, hooks.",
                       "The internal registry for AI artifacts: MCP servers, skills, agents, prompts, plugins, hooks."),
        ["signedin"] = ("Connecté en tant que", "Signed in as"),
        ["role"] = ("Rôle", "Role"),
        ["catalog"] = ("Catalogue", "Catalog"),
        ["soon"] = ("Disponible en phase 1", "Coming in phase 1"),
        ["theme"] = ("Thème", "Theme"),
        ["lang"] = ("Langue", "Language"),
        ["search"] = ("Rechercher un skill, un serveur MCP, un tag…", "Search a skill, MCP server, tag…"),
        ["all"] = ("Tous", "All"),
        ["families"] = ("Familles", "Families"),
        ["sort_recent"] = ("Plus récents", "Most recent"),
        ["sort_popular"] = ("Plus votés", "Most voted"),
        ["status"] = ("Statut", "Status"),
        ["publish"] = ("Publier", "Publish"),
        ["empty"] = ("Aucun artefact pour l'instant. Publiez le premier !", "No artifact yet. Publish the first one!"),
        ["back"] = ("Retour au catalogue", "Back to catalog"),
        ["download"] = ("Télécharger (.zip)", "Download (.zip)"),
        ["files"] = ("Fichiers", "Files"),
        ["history"] = ("Historique", "History"),
        ["integrity"] = ("Intégrité", "Integrity"),
        ["quality"] = ("Qualité", "Quality"),
        ["install"] = ("Installer", "Install"),
        ["approve"] = ("Approuver", "Approve"),
        ["deprecate"] = ("Déprécier", "Deprecate"),
        ["type"] = ("Type", "Type"),
        ["namespace"] = ("Namespace", "Namespace"),
        ["name"] = ("Nom", "Name"),
        ["version"] = ("Version (optionnelle)", "Version (optional)"),
        ["description"] = ("Description", "Description"),
        ["filename"] = ("Nom du fichier principal", "Main file name"),
        ["content"] = ("Contenu", "Content"),
        ["tags"] = ("Tags (séparés par des virgules)", "Tags (comma-separated)"),
        ["validate"] = ("Valider", "Validate"),
        ["load_file"] = ("Charger un fichier", "Load a file"),
        ["published"] = ("Publié en statut Pending", "Published as Pending"),
        ["refused"] = ("Publication refusée", "Publication refused"),
        ["canonical"] = ("Forme canonique proposée", "Suggested canonical name"),
        ["warnings"] = ("Avertissements", "Warnings"),
        ["not_found"] = ("Artefact introuvable.", "Artifact not found."),
        ["votes"] = ("Votes", "Votes"),
        ["content_tab"] = ("Contenu", "Content"),
        ["exports"] = ("Exports", "Exports"),
        ["export_cursor"] = ("Copier en .cursorrules", ".cursorrules"),
        ["export_claude"] = ("Config Claude Desktop", "Claude Desktop config"),
        ["export_audit"] = ("Prompt d'audit LLM", "LLM audit prompt"),
        ["report"] = ("Signaler", "Report"),
        ["report_reason"] = ("Motif", "Reason"),
        ["report_details"] = ("Détails (optionnel)", "Details (optional)"),
        ["report_sent"] = ("Signalement envoyé, merci.", "Report sent, thank you."),
        ["report_quarantined"] = ("Cette version a été mise en quarantaine.", "This version has been quarantined."),
        ["comments"] = ("Commentaires", "Comments"),
        ["comment_add"] = ("Ajouter un commentaire", "Add a comment"),
        ["comment_send"] = ("Envoyer", "Send"),
        ["no_comments"] = ("Aucun commentaire.", "No comments."),
        ["quarantine"] = ("Quarantaine", "Quarantine"),
        ["release"] = ("Lever la quarantaine", "Release quarantine"),
        ["my_pubs"] = ("Mes publications", "My publications"),
        ["unpublish"] = ("Retirer", "Unpublish"),
        ["no_pubs"] = ("Vous n'avez encore rien publié.", "You haven't published anything yet."),
        ["tag_filter"] = ("Tag", "Tag"),
        ["status_all"] = ("Tous statuts", "Any status"),
    };

    public string Lang { get; private set; } = "fr";
    public event Action? Changed;

    public string this[string key] =>
        Texts.TryGetValue(key, out var t) ? (Lang == "fr" ? t.Fr : t.En) : key;

    public void Toggle()
    {
        Lang = Lang == "fr" ? "en" : "fr";
        Changed?.Invoke();
    }
}
