using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.KnowledgeBase.Models;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.KnowledgeBase.Services;

/// <summary>
/// Les règles de la base de connaissances qui ne sont pas de l'affichage : archivage des
/// révisions, historisation des changements, comptage des consultations, et visibilité d'un
/// article pour un lecteur donné.
///
/// Regroupées ici plutôt que recopiées dans chaque page parce qu'elles doivent valoir partout :
/// un article modifié depuis un écran et un article modifié depuis un autre doivent laisser la
/// même trace, et un article invisible dans la liste ne doit pas devenir lisible en collant son
/// URL.
/// </summary>
public sealed class KnowledgeBaseService(
    IDbContextFactory<DbContext> dbFactory,
    IPrincipalContextProvider principalContext,
    IPrincipalDirectory directory,
    KnowledgeBaseViewTracker viewTracker)
{
    /// <summary>
    /// Enregistre un article (création ou modification), archive l'état antérieur dès que le
    /// sujet ou le contenu change, et trace dans l'historique tout ce qui a bougé.
    ///
    /// L'archivage est conditionné au changement de fond : cocher « FAQ » ou déplacer un article
    /// de catégorie ne crée pas de révision, sans quoi l'historique se remplirait de doublons
    /// entre lesquels personne ne saurait plus retrouver la modification qu'il cherche. Ces
    /// changements-là sont en revanche bien tracés dans l'historique, qui est fait pour ça.
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
                VisibleFrom = edited.VisibleFrom,
                VisibleUntil = edited.VisibleUntil,
                AuthorUserId = edited.AuthorUserId,
                AuthorName = editorName,
                LastEditorName = editorName,
                CreatedAt = DateTime.UtcNow,
            };

            db.Set<KnowledgeBaseArticle>().Add(created);
            await db.SaveChangesAsync(cancellationToken);

            db.Set<KnowledgeBaseArticleHistoryEntry>().Add(new KnowledgeBaseArticleHistoryEntry
            {
                ArticleId = created.Id,
                User = editorName,
                Field = "Article",
                Description = "Création de l'article.",
            });

            await db.SaveChangesAsync(cancellationToken);
            return created.Id;
        }

        KnowledgeBaseArticle? existing = await db.Set<KnowledgeBaseArticle>()
            .FirstOrDefaultAsync(article => article.Id == edited.Id, cancellationToken);

        if (existing is null)
        {
            return edited.Id;
        }

        string subject = edited.Subject.Trim();
        bool contentChanged = existing.Subject != subject || existing.Content != edited.Content;

        int? revisionNumber = null;

        if (contentChanged)
        {
            int lastNumber = await db.Set<KnowledgeBaseArticleRevision>()
                .Where(revision => revision.ArticleId == existing.Id)
                .MaxAsync(revision => (int?)revision.Number, cancellationToken) ?? 0;

            revisionNumber = lastNumber + 1;

            db.Set<KnowledgeBaseArticleRevision>().Add(new KnowledgeBaseArticleRevision
            {
                ArticleId = existing.Id,
                Number = revisionNumber.Value,
                Subject = existing.Subject,
                Content = existing.Content,
                EditorName = existing.LastEditorName ?? existing.AuthorName,
                RevisedAt = DateTime.UtcNow,
            });
        }

        // Les libellés de catégorie sont résolus avant l'écriture : après, l'ancienne valeur n'est
        // plus lisible nulle part, et « Catégorie : 4 → 7 » n'apprendrait rien à personne.
        List<KnowledgeBaseArticleHistoryEntry> history = await BuildFieldHistoryAsync(
            db, existing, edited, subject, revisionNumber, editorName, cancellationToken);

        existing.Subject = subject;
        existing.Content = edited.Content;
        existing.CategoryId = edited.CategoryId;
        existing.IsFaq = edited.IsFaq;
        existing.IsPinned = edited.IsPinned;
        existing.VisibleFrom = edited.VisibleFrom;
        existing.VisibleUntil = edited.VisibleUntil;

        if (contentChanged)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            existing.LastEditorName = editorName;
        }

        db.Set<KnowledgeBaseArticleHistoryEntry>().AddRange(history);

        await db.SaveChangesAsync(cancellationToken);
        return existing.Id;
    }

    /// <summary>
    /// Compare l'état enregistré et l'état soumis, et produit une ligne d'historique par champ
    /// modifié.
    ///
    /// Le contenu fait exception : il est résumé (« modifié, révision n°3 archivée ») au lieu
    /// d'être recopié. Un article fait couramment plusieurs milliers de caractères ; les coller
    /// dans l'historique le rendrait illisible et dupliquerait ce que la révision conserve déjà,
    /// elle, sous une forme qu'on peut restaurer.
    /// </summary>
    private async Task<List<KnowledgeBaseArticleHistoryEntry>> BuildFieldHistoryAsync(
        DbContext db,
        KnowledgeBaseArticle before,
        KnowledgeBaseArticle after,
        string subject,
        int? revisionNumber,
        string editorName,
        CancellationToken cancellationToken)
    {
        List<KnowledgeBaseArticleHistoryEntry> entries = [];

        void Record(string field, string description) => entries.Add(new KnowledgeBaseArticleHistoryEntry
        {
            ArticleId = before.Id,
            User = editorName,
            Field = field,
            Description = description,
        });

        if (before.Subject != subject)
        {
            Record("Sujet", $"« {before.Subject} » → « {subject} »");
        }

        if (before.Content != after.Content)
        {
            Record("Contenu", revisionNumber is int number
                ? $"Contenu modifié, révision n°{number} archivée."
                : "Contenu modifié.");
        }

        if (before.CategoryId != after.CategoryId)
        {
            Record("Catégorie",
                $"{await CategoryLabelAsync(db, before.CategoryId, cancellationToken)} → " +
                $"{await CategoryLabelAsync(db, after.CategoryId, cancellationToken)}");
        }

        if (before.IsFaq != after.IsFaq)
        {
            Record("FAQ", after.IsFaq ? "Publié dans la FAQ." : "Retiré de la FAQ.");
        }

        if (before.IsPinned != after.IsPinned)
        {
            Record("Épinglage", after.IsPinned ? "Article épinglé." : "Article désépinglé.");
        }

        if (before.VisibleFrom != after.VisibleFrom)
        {
            Record("Début de visibilité", $"{DateLabel(before.VisibleFrom)} → {DateLabel(after.VisibleFrom)}");
        }

        if (before.VisibleUntil != after.VisibleUntil)
        {
            Record("Fin de visibilité", $"{DateLabel(before.VisibleUntil)} → {DateLabel(after.VisibleUntil)}");
        }

        return entries;
    }

    private static async Task<string> CategoryLabelAsync(DbContext db, int? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is not int id)
        {
            return "(aucune)";
        }

        string? name = await db.Set<KnowledgeBaseCategory>()
            .Where(category => category.Id == id)
            .Select(category => category.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return name is null ? $"#{id} (supprimée)" : $"« {name} »";
    }

    private static string DateLabel(DateTime? date) => date?.ToString("dd/MM/yyyy") ?? "(aucune)";

    /// <summary>
    /// Remplace les cibles de visibilité d'un article par celles fournies, et trace dans
    /// l'historique celles qui entrent et celles qui sortent. Remplacement complet plutôt
    /// qu'ajout/retrait ligne à ligne : l'écran présente la liste entière, c'est donc la liste
    /// entière qui fait foi.
    /// </summary>
    public async Task SetTargetsAsync(
        int articleId,
        IReadOnlyList<KnowledgeBaseTargetSpec> targets,
        string editorName,
        CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        List<KnowledgeBaseArticleTarget> existing = await db.Set<KnowledgeBaseArticleTarget>()
            .Where(target => target.ArticleId == articleId)
            .ToListAsync(cancellationToken);

        HashSet<KnowledgeBaseTargetSpec> before =
            [.. existing.Select(target => new KnowledgeBaseTargetSpec(target.Type, target.ItemId, target.ScopeEntityId, target.IsRecursive))];

        HashSet<KnowledgeBaseTargetSpec> after = [.. targets];

        db.Set<KnowledgeBaseArticleTarget>().RemoveRange(existing);

        foreach (KnowledgeBaseTargetSpec target in after)
        {
            db.Set<KnowledgeBaseArticleTarget>().Add(new KnowledgeBaseArticleTarget
            {
                ArticleId = articleId,
                Type = target.Kind,
                ItemId = target.ItemId,
                ScopeEntityId = target.ScopeEntityId,
                IsRecursive = target.IsRecursive,
            });
        }

        foreach (KnowledgeBaseArticleHistoryEntry entry in
                 await BuildTargetHistoryAsync(articleId, before, after, editorName, cancellationToken))
        {
            db.Set<KnowledgeBaseArticleHistoryEntry>().Add(entry);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Une ligne d'historique par cible ajoutée ou retirée, avec le nom affiché de l'acteur visé
    /// plutôt que son identifiant : l'intérêt de la trace est précisément de rester lisible après
    /// que le groupe a été renommé ou supprimé.
    /// </summary>
    private async Task<List<KnowledgeBaseArticleHistoryEntry>> BuildTargetHistoryAsync(
        int articleId,
        HashSet<KnowledgeBaseTargetSpec> before,
        HashSet<KnowledgeBaseTargetSpec> after,
        string editorName,
        CancellationToken cancellationToken)
    {
        List<KnowledgeBaseTargetSpec> added = [.. after.Except(before)];
        List<KnowledgeBaseTargetSpec> removed = [.. before.Except(after)];

        if (added.Count == 0 && removed.Count == 0)
        {
            return [];
        }

        // Les annuaires sont interrogés une fois par type présent, pas une fois par cible.
        Dictionary<(PrincipalKind, int), string> names = [];

        foreach (PrincipalKind kind in added.Concat(removed).Select(target => target.Kind).Distinct())
        {
            foreach (PrincipalOption option in await directory.GetAsync(kind, cancellationToken))
            {
                names[(kind, option.Id)] = option.Name;
            }
        }

        Dictionary<int, string> entityNames = [];

        if (added.Concat(removed).Any(target => target.ScopeEntityId is not null))
        {
            foreach (PrincipalOption option in await directory.GetAsync(PrincipalKind.Entity, cancellationToken))
            {
                entityNames[option.Id] = option.Name;
            }
        }

        return
        [
            .. removed.Select(target => Entry("Cible retirée", target)),
            .. added.Select(target => Entry("Cible ajoutée", target)),
        ];

        KnowledgeBaseArticleHistoryEntry Entry(string field, KnowledgeBaseTargetSpec target)
            => new()
            {
                ArticleId = articleId,
                User = editorName,
                Field = field,
                Description = Describe(target),
            };

        string Describe(KnowledgeBaseTargetSpec target)
        {
            string name = names.TryGetValue((target.Kind, target.ItemId), out string? found)
                ? found
                : $"#{target.ItemId}";

            string text = $"{KindLabel(target.Kind)} « {name} »";

            if (target.ScopeEntityId is int entityId)
            {
                string entity = entityNames.TryGetValue(entityId, out string? entityName) ? entityName : $"#{entityId}";
                text += $", dans l'entité « {entity} »";
            }

            return target.IsRecursive ? $"{text} (et sous-entités)" : text;
        }
    }

    /// <summary>Libellé humain d'un type de cible, partagé par l'historique et les écrans.</summary>
    public static string KindLabel(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Entity => "Entité",
        PrincipalKind.Group => "Groupe",
        PrincipalKind.Profile => "Profil",
        PrincipalKind.User => "Utilisateur",
        _ => kind.ToString(),
    };

    /// <summary>Historique d'un article, du plus récent au plus ancien.</summary>
    public async Task<List<KnowledgeBaseArticleHistoryEntry>> GetHistoryAsync(int articleId, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Set<KnowledgeBaseArticleHistoryEntry>()
            .AsNoTracking()
            .Where(entry => entry.ArticleId == articleId)
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Compte une consultation, au plus une fois par lecteur et par article sur une courte période
    /// — voir <see cref="KnowledgeBaseViewTracker"/> pour la raison.
    ///
    /// L'incrément est fait en base sans relire l'article : deux lecteurs simultanés ne doivent pas
    /// s'écraser l'un l'autre, ce qu'un « lire, +1, enregistrer » ferait immanquablement. Il ne
    /// laisse volontairement pas de trace dans l'historique : une lecture n'est pas une
    /// modification, et l'y écrire noierait les changements sous le passage des lecteurs.
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
    /// L'auteur voit toujours son article, avant toute autre règle : sans cette exception, un
    /// rédacteur pourrait se fermer l'accès au sien en le ciblant sur un groupe dont il ne fait
    /// pas partie, ou en le datant pour plus tard, et ne plus pouvoir le corriger.
    ///
    /// Vient ensuite la période de visibilité (<see cref="KnowledgeBaseArticle.VisibleFrom"/> /
    /// <see cref="KnowledgeBaseArticle.VisibleUntil"/>), puis les cibles. Sans cible, l'article est
    /// visible de tous ceux qui voient la base — c'est le cas courant et le défaut de GLPI, le
    /// cloisonnement par entité s'appliquant de toute façon en amont.
    /// </summary>
    /// <param name="article">Article évalué, ses cibles chargées.</param>
    /// <param name="viewer">Habilitations du lecteur, ou <c>null</c> s'il n'est pas identifié.</param>
    /// <param name="asOf">Instant d'évaluation ; <c>null</c> = maintenant. Paramétrable pour que
    /// la règle reste vérifiable sans attendre une date.</param>
    public static bool IsVisible(KnowledgeBaseArticle article, PrincipalContext? viewer, DateTime? asOf = null)
    {
        if (viewer is not null && article.AuthorUserId == viewer.UserId)
        {
            return true;
        }

        if (!IsPublished(article, asOf ?? DateTime.UtcNow))
        {
            return false;
        }

        if (article.Targets.Count == 0)
        {
            return true;
        }

        if (viewer is null)
        {
            return false;
        }

        return article.Targets.Any(target =>
            viewer.Matches(target.Type, target.ItemId, target.ScopeEntityId, target.IsRecursive));
    }

    /// <summary>
    /// Vrai si <paramref name="asOf"/> tombe dans la période de visibilité de l'article. Les deux
    /// bornes sont facultatives et indépendantes : un article peut n'avoir qu'une date de début
    /// (publication différée) ou qu'une date de fin (consigne temporaire).
    /// </summary>
    public static bool IsPublished(KnowledgeBaseArticle article, DateTime asOf)
        => (article.VisibleFrom is not DateTime from || asOf >= from)
            && (article.VisibleUntil is not DateTime until || asOf <= until);

    /// <summary>
    /// Supprime un article, ses révisions, ses cibles et son historique. Suppression définitive et
    /// non mise à la corbeille : GlpiNg n'a pas de corbeille pour la base de connaissances (voir
    /// le README), l'écran demande donc confirmation.
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

        // Révisions, cibles et historique partent avec l'article par cascade (voir la
        // configuration du DbContext hôte) : les retirer ici ferait double emploi.
        db.Set<KnowledgeBaseArticle>().Remove(article);
        await db.SaveChangesAsync(cancellationToken);
    }
}
