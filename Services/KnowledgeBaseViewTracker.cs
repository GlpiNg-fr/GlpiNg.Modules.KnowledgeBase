using System.Collections.Concurrent;

namespace GlpiNg.Modules.KnowledgeBase.Services;

/// <summary>
/// Mémoire des consultations déjà comptées, partagée par toute l'application (singleton).
///
/// Sans elle, le compteur de vues d'un article serait faux de bout en bout : une page Blazor
/// Server est rendue deux fois à l'ouverture (pré-rendu HTTP puis démarrage du circuit), un
/// rafraîchissement du navigateur la rouvre, et le moindre aller-retour sur l'onglet « Révisions »
/// la rouvrirait encore. Un article lu une fois compterait pour trois ou quatre, et « les articles
/// les plus consultés » ne classerait plus que les articles dont on ouvre souvent les onglets.
///
/// La fenêtre d'oubli est délibérément courte : elle vise les doublons techniques et les allers-
/// retours immédiats, pas à interdire de recompter la lecture du lendemain.
/// </summary>
public sealed class KnowledgeBaseViewTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>Au-delà de cette taille, les entrées expirées sont balayées : la mémoire ne doit pas
    /// croître avec le nombre de couples (lecteur, article) vus depuis le démarrage.</summary>
    private const int PurgeThreshold = 2000;

    private readonly ConcurrentDictionary<(int UserId, int ArticleId), DateTime> _lastCounted = new();

    /// <summary>
    /// Vrai si cette consultation doit être comptée, et la mémorise dans ce cas. Appelée une fois
    /// par ouverture de fiche.
    /// </summary>
    public bool ShouldCount(int userId, int articleId, DateTime now)
    {
        if (_lastCounted.Count > PurgeThreshold)
        {
            Purge(now);
        }

        bool count = false;

        _lastCounted.AddOrUpdate(
            (userId, articleId),
            _ =>
            {
                count = true;
                return now;
            },
            (_, previous) =>
            {
                if (now - previous < Window)
                {
                    return previous;
                }

                count = true;
                return now;
            });

        return count;
    }

    private void Purge(DateTime now)
    {
        foreach (KeyValuePair<(int UserId, int ArticleId), DateTime> entry in _lastCounted)
        {
            if (now - entry.Value >= Window)
            {
                _lastCounted.TryRemove(entry.Key, out _);
            }
        }
    }
}
