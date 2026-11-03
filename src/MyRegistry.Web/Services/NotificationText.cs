using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Services;

public static class NotificationText
{
    public static string Compose(NotificationDto n, bool fr) => n.Kind switch
    {
        NotificationKind.StatusChanged => fr ? $"« {n.Subject} » est maintenant {n.Detail}" : $"“{n.Subject}” is now {n.Detail}",
        NotificationKind.NewSubmission => fr ? $"Nouvelle soumission à modérer : {n.Subject}" : $"New submission to moderate: {n.Subject}",
        NotificationKind.ReportFiled => fr ? $"Signalement ({n.Detail}) sur {n.Subject}" : $"Report ({n.Detail}) on {n.Subject}",
        NotificationKind.CommentAdded => fr ? $"Nouveau commentaire sur {n.Subject}" : $"New comment on {n.Subject}",
        _ => n.Subject,
    };

    public static string KindLabel(NotificationKind k, bool fr) => k switch
    {
        NotificationKind.StatusChanged => fr ? "Changement de statut de mes artefacts" : "Status changes on my artifacts",
        NotificationKind.NewSubmission => fr ? "Nouvelles soumissions (modération)" : "New submissions (moderation)",
        NotificationKind.ReportFiled => fr ? "Signalements (modération)" : "Reports (moderation)",
        _ => fr ? "Commentaires sur mes artefacts" : "Comments on my artifacts",
    };
}
