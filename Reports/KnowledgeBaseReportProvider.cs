using GlpiNg.Modules.Abstractions.Reports;
using GlpiNg.Modules.KnowledgeBase.Models;
using GlpiNg.Modules.KnowledgeBase.Services;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.KnowledgeBase.Reports;

/// <summary>
/// Rapport du module, affiché avec les autres sur <c>/tools/reports</c> — voir
/// <see cref="IReportProvider"/>.
///
/// Il répond à la question qu'on se pose une fois la base remplie : est-elle lue ? Une base de
/// connaissances se dégrade en silence — les articles y restent, mais personne ne les ouvre plus,
/// et rien à l'écran ne le dit. D'où les deux tableaux de détail : ce qui sert, et ce qui dort.
///
/// Le rapport ne tient pas compte des cibles de visibilité : il est destiné à qui administre la
/// base, et masquer des articles fausserait les totaux qu'il est censé donner. Le cloisonnement
/// par entité, lui, s'applique comme partout.
/// </summary>
public sealed class KnowledgeBaseReportProvider(IDbContextFactory<DbContext> dbFactory) : IReportProvider
{
    internal const string OverviewKey = "kb-overview";

    /// <summary>Nombre de lignes des tableaux de détail : un palmarès, pas la liste des articles,
    /// qui a déjà sa page.</summary>
    private const int DetailRowLimit = 20;

    private static readonly ReportDefinition[] Definitions =
    [
        new(OverviewKey, "Base de connaissances",
            "Articles par catégorie, les plus consultés, et ceux que personne n'ouvre.",
            "ti-book", "Base de connaissances"),
    ];

    public IReadOnlyList<ReportDefinition> GetReports() => Definitions;

    public Task<IReadOnlyList<ReportFilter>> GetFiltersAsync(string reportKey, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReportFilter>>(reportKey == OverviewKey
            ? [ReportFilter.Select("faq", "Périmètre",
                [
                    new("all", "Tous les articles"),
                    new("faq", "FAQ uniquement"),
                ], "all")]
            : []);

    public async Task<ReportResult?> RunAsync(string reportKey, ReportParameters parameters, CancellationToken cancellationToken = default)
    {
        if (reportKey != OverviewKey)
        {
            return null;
        }

        bool faqOnly = parameters.GetString("faq") == "faq";

        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        List<KnowledgeBaseCategory> categories = await db.Set<KnowledgeBaseCategory>()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var articles = await db.Set<KnowledgeBaseArticle>()
            .AsNoTracking()
            .Where(article => !faqOnly || article.IsFaq)
            .Select(article => new
            {
                article.Id,
                article.Subject,
                article.CategoryId,
                article.IsFaq,
                article.ViewCount,
                article.AuthorName,
                article.CreatedAt,
                article.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        int total = articles.Count;

        // Deux comptes par catégorie, et non un seul : les articles qui y sont rangés directement,
        // et ceux de toute sa branche. Le premier est celui qui se totalise (chaque article n'y
        // figure qu'une fois, la part se lit donc sur 100 %) ; le second évite qu'une catégorie
        // intermédiaire, dont tous les articles sont dans ses sous-catégories, passe pour morte.
        // Les branches sont calculées une fois pour toutes — les recalculer par article referait le
        // même parcours d'arbre des milliers de fois.
        Dictionary<int, HashSet<int>> branches = categories.ToDictionary(
            category => category.Id,
            category => CategoryTree.Branch(categories, category.Id));

        List<ReportRow> byCategory = categories
            .Select(category => new
            {
                category.Name,
                Direct = articles.Where(article => article.CategoryId == category.Id).ToList(),
                BranchCount = articles.Count(article => article.CategoryId is int id && branches[category.Id].Contains(id)),
            })
            .Where(entry => entry.BranchCount > 0)
            .OrderByDescending(entry => entry.BranchCount)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new ReportRow(
            [
                ReportCell.Of(entry.Name),
                ReportCell.Of(entry.Direct.Count),
                ReportCell.Of(entry.BranchCount),
                ReportCell.Of(entry.Direct.Count(article => article.IsFaq)),
                ReportCell.Of(entry.Direct.Sum(article => article.ViewCount)),
                ReportCell.Percent(entry.Direct.Count, total),
            ]))
            .ToList();

        int uncategorized = articles.Count(article => article.CategoryId is null);

        if (uncategorized > 0)
        {
            byCategory.Add(new ReportRow(
            [
                ReportCell.Of("Sans catégorie"),
                ReportCell.Of(uncategorized),
                ReportCell.Of(uncategorized),
                ReportCell.Of(articles.Count(article => article.CategoryId is null && article.IsFaq)),
                ReportCell.Of(articles.Where(article => article.CategoryId is null).Sum(article => article.ViewCount)),
                ReportCell.Percent(uncategorized, total),
            ]));
        }

        if (byCategory.Count > 0)
        {
            byCategory.Add(new ReportRow(
            [
                ReportCell.Of("Total"),
                ReportCell.Of(total),
                ReportCell.Of(total),
                ReportCell.Of(articles.Count(article => article.IsFaq)),
                ReportCell.Of(articles.Sum(article => article.ViewCount)),
                ReportCell.Percent(total, total),
            ], IsTotal: true));
        }

        List<ReportRow> mostRead = [.. articles
            .OrderByDescending(article => article.ViewCount)
            .ThenBy(article => article.Subject, StringComparer.CurrentCultureIgnoreCase)
            .Take(DetailRowLimit)
            .Select(article => new ReportRow(
            [
                ReportCell.Link(article.Subject, $"/tools/knowledgebase/article/{article.Id}"),
                ReportCell.Of(CategoryName(categories, article.CategoryId)),
                ReportCell.Of(article.ViewCount),
                ReportCell.Date(article.UpdatedAt ?? article.CreatedAt),
            ]))];

        var neverRead = articles.Where(article => article.ViewCount == 0).ToList();

        List<ReportRow> unread = [.. neverRead
            .OrderBy(article => article.CreatedAt)
            .Take(DetailRowLimit)
            .Select(article => new ReportRow(
            [
                ReportCell.Link(article.Subject, $"/tools/knowledgebase/article/{article.Id}"),
                ReportCell.Of(CategoryName(categories, article.CategoryId)),
                ReportCell.Of(article.AuthorName),
                ReportCell.Date(article.CreatedAt),
            ]))];

        return ReportResult.Of(
            new ReportTable("Articles par catégorie",
            [
                new("Catégorie"),
                new("Articles", ReportColumnKind.Number),
                new("Avec sous-catégories", ReportColumnKind.Number),
                new("Dont FAQ", ReportColumnKind.Number),
                new("Consultations", ReportColumnKind.Number),
                new("Part de la base", ReportColumnKind.Share),
            ], byCategory,
                "Aucun article dans le périmètre choisi."),
            new ReportTable($"Les {DetailRowLimit} articles les plus consultés",
            [
                new("Article"),
                new("Catégorie"),
                new("Consultations", ReportColumnKind.Number),
                new("Dernière modification"),
            ], mostRead,
                "Aucun article dans le périmètre choisi."),
            new ReportTable(
                neverRead.Count > DetailRowLimit
                    ? $"Articles jamais consultés — {DetailRowLimit} plus anciens sur {neverRead.Count}"
                    : "Articles jamais consultés",
            [
                new("Article"),
                new("Catégorie"),
                new("Auteur"),
                new("Créé le"),
            ], unread,
                "Tous les articles du périmètre ont été consultés au moins une fois."));
    }

    private static string CategoryName(IReadOnlyList<KnowledgeBaseCategory> categories, int? categoryId)
        => categoryId is int id && categories.FirstOrDefault(category => category.Id == id) is { } category
            ? category.Name
            : "Sans catégorie";
}
