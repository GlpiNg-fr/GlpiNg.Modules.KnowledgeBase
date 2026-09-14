using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.KnowledgeBase.Models;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.KnowledgeBase.Services;

/// <summary>
/// Les règles de la base de connaissances qui ne sont pas de l'affichage : archivage des
/// révisions, comptage des consultations, et visibilité d'un article pour un lecteur donné.
///
/// Regroupées ici plutôt que recopiées dans chaque page parce qu'elles doivent valoir partout :
/// un article modifié depuis un écran et un article modifié depuis un autre doivent laisser la
/// même trace, et un article invisible dans la liste ne doit pas devenir lisible en collant son
/// URL.
/// </summary>
public sealed class KnowledgeBaseService(
    IDbContextFactory<DbContext> dbFactory,
    IPrincipalContextProvider principalContext,
    KnowledgeBaseViewTracker viewTracker)
{
    /// <summary>
    /// Enregistre un article (création ou modification) et archive l'état antérieur dès que le
    /// sujet ou le contenu change.
    ///
    /// L'archivage est conditionné au changement de fond : cocher « FAQ » ou déplacer un article
    /// de catégorie ne crée pas de révision, sans quoi l'historique se remplirait de doublons
    /// entre lesquels personne ne saurait plus retrouver la modification qu'il cherche.
    /// </summary>
    /// <returns>Identifiant de l'article enregistré.</returns>
    public async Task<int> SaveAsync(KnowledgeBaseArticle edited, string editorName, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (edited.Id == 0)
        {
            KnowledgeBaseArticle created = new()
            {
                Subject = edited.Subject.Trim(),
                Content = edited.Content,
                CategoryId = edited.CategoryId,
                IsFaq = edited.IsFaq,
                IsPinned = edited.IsPinned,
                AuthorUserId = edited.AuthorUserId,
                AuthorName = editorName,
                LastEditorName = editorName,
                CreatedAt = DateTime.UtcNow,
            };

            db.Set<KnowledgeBaseArticle>().Add(created);
            await db.SaveChangesAsync(cancellationToken);
            return created.Id;
        }

        KnowledgeBaseArticle? existing = await db.Set<KnowledgeBaseArticle>()
            .FirstOrDefaultAsync(article => article.Id == edited.Id, cancellationToken);

        if (existing is null)
        {
            return edited.Id;
        }

        bool contentChanged = existing.Subject != edited.Subject.Trim() || existing.Content != edited.Content;

        if (contentChanged)
        {
            int lastNumber = await db.Set<KnowledgeBaseArticleRevision>()
                .Where(revision => revision.ArticleId == existing.Id)
                .MaxAsync(revision => (int?)revision.Number, cancellationToken) ?? 0;

            db.Set<KnowledgeBaseArticleRevision>().Add(new KnowledgeBaseArticleRevision
            {
                ArticleId = existing.Id,
                Number = lastNumber + 1,
                Subject = existing.Subject,
                Content = existing.Content,
                EditorName = existing.LastEditorName ?? existing.AuthorName,
                RevisedAt = DateTime.UtcNow,
            });
        }

        existing.Subject = edited.Subject.Trim();
        existing.Content = edited.Content;
        existing.CategoryId = edited.CategoryId;
        existing.IsFaq = edited.IsFaq;
        existing.IsPinned = edited.IsPinned;

        if (contentChanged)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            existing.LastEditorName = editorName;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing.Id;
    }

    /// <summary>
    /// Remplace les cibles de visibilité d'un article par celles fournies. Remplacement complet
    /// plutôt qu'ajout/retrait ligne à ligne : l'écran présente la liste entière, c'est donc la
    /// liste entière qui fait foi.
    /// </summary>
    public async Task SetTargetsAsync(int articleId, IReadOnlyList<(PrincipalKind Kind, int ItemId)> targets, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        List<KnowledgeBaseArticleTarget> existing = await db.Set<KnowledgeBaseArticleTarget>()
            .Where(target => target.ArticleId == articleId)
            .ToListAsync(cancellationToken);

        db.Set<KnowledgeBaseArticleTarget>().RemoveRange(existing);

        foreach ((PrincipalKind kind, int itemId) in targets.Distinct())
        {
            db.Set<KnowledgeBaseArticleTarget>().Add(new KnowledgeBaseArticleTarget
            {
                ArticleId = articleId,
                Type = kind,
                ItemId = itemId,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Compte une consultation, au plus une fois par lecteur et par article sur une courte période
    /// — voir <see cref="KnowledgeBaseViewTracker"/> pour la raison.
    ///
    /// L'incrément est fait en base sans relire l'article : deux lecteurs simultanés ne doivent pas
    /// s'écraser l'un l'autre, ce qu'un « lire, +1, enregistrer » ferait immanquablement.
    /// </summary>
    public async Task RegisterViewAsync(int articleId, int userId, CancellationToken cancellationToken = default)
    {
        if (!viewTracker.ShouldCount(userId, articleId, DateTime.UtcNow))
        {
            return;
        }

        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        await db.Set<KnowledgeBaseArticle>()
            .Where(article => article.Id == articleId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(article => article.ViewCount, article => article.ViewCount + 1), cancellationToken);
    }

    /// <summary>Habilitations du lecteur, pour évaluer les cibles des articles.</summary>
    public Task<PrincipalContext?> GetViewerAsync(int userId, CancellationToken cancellationToken = default)
        => principalContext.GetAsync(userId, cancellationToken);

    /// <summary>
    /// Décide si <paramref name="article"/> est lisible par <paramref name="viewer"/>. L'article
    /// doit avoir ses <see cref="KnowledgeBaseArticle.Targets"/> chargées : un article dont on
    /// n'aurait pas chargé les cibles passerait pour ouvert à tous.
    ///
    /// Sans cible, l'article est visible de tous ceux qui voient la base — c'est le cas courant et
    /// le défaut de GLPI, le cloisonnement par entité s'appliquant de toute façon en amont. Avec
    /// des cibles, seul un lecteur visé y a accès, plus l'auteur : sans cette exception, un
    /// rédacteur pourrait se fermer l'accès à son propre article en le ciblant sur un groupe dont
    /// il ne fait pas partie, et ne plus pouvoir le corriger.
    /// </summary>
    public static bool IsVisible(KnowledgeBaseArticle article, PrincipalContext? viewer)
    {
        if (article.Targets.Count == 0)
        {
            return true;
        }

        if (viewer is null)
        {
            return false;
        }

        return article.AuthorUserId == viewer.UserId
            || article.Targets.Any(target => viewer.Matches(target.Type, target.ItemId));
    }

    /// <summary>
    /// Supprime un article, ses révisions et ses cibles. Suppression définitive et non mise à la
    /// corbeille : GlpiNg n'a pas de corbeille pour la base de connaissances (voir le README),
    /// l'écran demande donc confirmation.
    /// </summary>
    public async Task DeleteArticleAsync(int articleId, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        KnowledgeBaseArticle? article = await db.Set<KnowledgeBaseArticle>()
            .FirstOrDefaultAsync(item => item.Id == articleId, cancellationToken);

        if (article is null)
        {
            return;
        }

        // Révisions et cibles partent avec l'article par cascade (voir la configuration du
        // DbContext hôte) : les retirer ici ferait double emploi.
        db.Set<KnowledgeBaseArticle>().Remove(article);
        await db.SaveChangesAsync(cancellationToken);
    }
}
