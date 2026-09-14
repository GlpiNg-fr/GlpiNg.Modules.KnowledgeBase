using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Preferences;
using GlpiNg.Modules.KnowledgeBase.Models;
using GlpiNg.Modules.KnowledgeBase.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.KnowledgeBase.Components.Pages.KnowledgeBase;

/// <summary>
/// Consultation de la base de connaissances : arbre des catégories à gauche, articles à droite,
/// recherche plein texte et tri. C'est la page d'entrée du module.
/// </summary>
public partial class Index : ComponentBase
{
    /// <summary>Identifiant fictif de la pseudo-catégorie « Sans catégorie ». Négatif pour ne
    /// jamais entrer en collision avec un identifiant réel.</summary>
    private const int UncategorizedId = -1;

    private static readonly int[] PageSizeOptions = [25, 50, 100, 200];

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private KnowledgeBaseService Service { get; set; } = null!;

    [Inject]
    private IUserPreferences UserPreferences { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthStateTask { get; set; }

    private List<KnowledgeBaseCategory> _categories = [];
    private List<(KnowledgeBaseCategory Category, int Depth)> _categoryTree = [];

    /// <summary>Articles que le lecteur a le droit de voir — voir <see cref="KnowledgeBaseService.IsVisible"/>.</summary>
    private List<KnowledgeBaseArticle> _visibleArticles = [];

    private List<KnowledgeBaseArticle> _filtered = [];
    private List<KnowledgeBaseArticle> _paged = [];

    private string _search = string.Empty;
    private string _sort = "recent";
    private bool _faqOnly;
    private int? _selectedCategoryId;
    private int _uncategorizedCount;

    private int _pageSize = 25;
    private int _currentPage = 1;

    private int TotalPages => _filtered.Count == 0 ? 1 : (int)Math.Ceiling(_filtered.Count / (double)_pageSize);

    protected override async Task OnInitializedAsync()
    {
        _pageSize = (await UserPreferences.GetAsync()).ItemsPerPage;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _categories = await db.Set<KnowledgeBaseCategory>()
            .AsNoTracking()
            .OrderBy(category => category.Name)
            .ToListAsync();

        // Tout est chargé puis filtré en mémoire : la visibilité d'un article dépend de ses cibles
        // et des habilitations du lecteur (groupes, profils), que la base ne sait pas croiser
        // elle-même. Le volume d'une base de connaissances — quelques centaines à quelques milliers
        // d'articles — le permet sans détour.
        List<KnowledgeBaseArticle> all = await db.Set<KnowledgeBaseArticle>()
            .AsNoTracking()
            .Include(article => article.Category)
            .Include(article => article.Targets)
            .ToListAsync();

        PrincipalContext? viewer = await ResolveViewerAsync();
        _visibleArticles = [.. all.Where(article => KnowledgeBaseService.IsVisible(article, viewer))];
        _uncategorizedCount = _visibleArticles.Count(article => article.CategoryId is null);

        _categoryTree = CategoryTree.Flatten(_categories);
        _currentPage = 1;
        ApplyFilter();
    }

    private async Task<PrincipalContext?> ResolveViewerAsync()
    {
        if (AuthStateTask is null)
        {
            return null;
        }

        AuthenticationState authState = await AuthStateTask;
        string? userIdClaim = authState.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return int.TryParse(userIdClaim, out int userId)
            ? await Service.GetViewerAsync(userId)
            : null;
    }

    /// <summary>Nombre d'articles visibles d'une catégorie, ses sous-catégories comprises — sans quoi
    /// une catégorie intermédiaire afficherait zéro alors qu'elle mène à des dizaines d'articles.</summary>
    private int CountFor(int categoryId)
    {
        HashSet<int> branch = CategoryTree.Branch(_categories, categoryId);
        return _visibleArticles.Count(article => article.CategoryId is int id && branch.Contains(id));
    }

    private void OnSearchInput(string? value)
    {
        _search = value ?? string.Empty;
        _currentPage = 1;
        ApplyFilter();
    }

    private void SetSort(string? sort)
    {
        _sort = sort ?? "recent";
        ApplyFilter();
    }

    private void SetFaqOnly(bool faqOnly)
    {
        _faqOnly = faqOnly;
        _currentPage = 1;
        ApplyFilter();
    }

    private void SelectCategory(int? categoryId)
    {
        _selectedCategoryId = categoryId;
        _currentPage = 1;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<KnowledgeBaseArticle> matched = _visibleArticles;

        if (_selectedCategoryId == UncategorizedId)
        {
            matched = matched.Where(article => article.CategoryId is null);
        }
        else if (_selectedCategoryId is int categoryId)
        {
            HashSet<int> branch = CategoryTree.Branch(_categories, categoryId);
            matched = matched.Where(article => article.CategoryId is int id && branch.Contains(id));
        }

        if (_faqOnly)
        {
            matched = matched.Where(article => article.IsFaq);
        }

        if (!string.IsNullOrWhiteSpace(_search))
        {
            string term = _search.Trim();
            matched = matched.Where(article =>
                article.Subject.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || article.Content.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        // Les articles épinglés passent devant quel que soit le tri : c'est tout ce qu'on attend
        // d'un épinglage, et un tri qui les renverrait en page 3 le viderait de son sens.
        IOrderedEnumerable<KnowledgeBaseArticle> sorted = matched.OrderByDescending(article => article.IsPinned);

        _filtered = _sort switch
        {
            "views" => [.. sorted.ThenByDescending(article => article.ViewCount).ThenBy(article => article.Subject, StringComparer.CurrentCultureIgnoreCase)],
            "alpha" => [.. sorted.ThenBy(article => article.Subject, StringComparer.CurrentCultureIgnoreCase)],
            _ => [.. sorted.ThenByDescending(article => article.UpdatedAt ?? article.CreatedAt)],
        };

        ApplyPaging();
    }

    private void ApplyPaging()
    {
        _currentPage = Math.Clamp(_currentPage, 1, TotalPages);
        _paged = [.. _filtered.Skip((_currentPage - 1) * _pageSize).Take(_pageSize)];
    }

    private void SetPageSize(int pageSize)
    {
        if (_pageSize == pageSize)
        {
            return;
        }

        _pageSize = pageSize;
        _currentPage = 1;
        ApplyPaging();
    }

    private void GoToPage(int page)
    {
        int target = Math.Clamp(page, 1, TotalPages);

        if (target == _currentPage)
        {
            return;
        }

        _currentPage = target;
        ApplyPaging();
    }

    /// <summary>
    /// Début du contenu, sur une seule ligne : de quoi reconnaître l'article sans l'ouvrir. La
    /// syntaxe Markdown est retirée d'abord — un extrait plein d'astérisques et de crochets se lit
    /// plus mal que le texte qu'il résume.
    /// </summary>
    private static string Excerpt(KnowledgeBaseArticle article)
    {
        string flat = MarkdownRenderer.ToPlainText(article.Content);

        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }

        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    private static string LastChange(KnowledgeBaseArticle article)
        => (article.UpdatedAt ?? article.CreatedAt).ToLocalTime().ToString("dd/MM/yyyy");
}
