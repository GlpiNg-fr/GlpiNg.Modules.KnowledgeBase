using BlazorBootstrap;
using GlpiNg.Modules.KnowledgeBase.Models;
using GlpiNg.Modules.KnowledgeBase.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.KnowledgeBase.Components.Pages.KnowledgeBase;

/// <summary>
/// Gestion de l'arborescence des catégories : création, renommage, déplacement, suppression.
/// Écran d'administration de la base, atteint depuis la page des articles.
/// </summary>
public partial class Categories : ComponentBase
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    private List<KnowledgeBaseCategory> _categories = [];
    private List<(KnowledgeBaseCategory Category, int Depth)> _tree = [];

    /// <summary>Nombre d'articles rattachés directement à chaque catégorie.</summary>
    private Dictionary<int, int> _articleCounts = [];

    private string _newName = string.Empty;
    private string _newParentId = string.Empty;
    private string _newComment = string.Empty;

    private int? _editingId;
    private string _editName = string.Empty;
    private string _editParentId = string.Empty;
    private string _editComment = string.Empty;

    private int? _confirmDeleteId;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _categories = await db.Set<KnowledgeBaseCategory>()
            .AsNoTracking()
            .ToListAsync();

        _articleCounts = await db.Set<KnowledgeBaseArticle>()
            .AsNoTracking()
            .Where(article => article.CategoryId != null)
            .GroupBy(article => article.CategoryId!.Value)
            .Select(group => new { CategoryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.CategoryId, entry => entry.Count);

        _tree = CategoryTree.Flatten(_categories);
        _editingId = null;
        _confirmDeleteId = null;
    }

    private int ArticleCount(int categoryId) => _articleCounts.GetValueOrDefault(categoryId);

    private int DirectChildCount(int categoryId) => _categories.Count(category => category.ParentId == categoryId);

    /// <summary>Où atterriront les articles d'une catégorie supprimée — voir <see cref="DeleteAsync"/>.</summary>
    private string ParentLabel(KnowledgeBaseCategory category)
        => category.ParentId is int parentId && _categories.FirstOrDefault(item => item.Id == parentId) is { } parent
            ? $"« {parent.Name} »"
            : Tr.T("« Sans catégorie »");

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newName))
        {
            return;
        }

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            db.Set<KnowledgeBaseCategory>().Add(new KnowledgeBaseCategory
            {
                Name = _newName.Trim(),
                Comment = string.IsNullOrWhiteSpace(_newComment) ? null : _newComment.Trim(),
                ParentId = int.TryParse(_newParentId, out int parentId) ? parentId : null,
            });

            await db.SaveChangesAsync();

            _newName = string.Empty;
            _newComment = string.Empty;
            _newParentId = string.Empty;

            ToastService.Notify(new ToastMessage(ToastType.Success, Tr.T("Catégorie créée.")));
            await LoadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            ToastService.Notify(new ToastMessage(ToastType.Danger, Tr.T("Échec de la création : {0}", ex.Message)));
        }
    }

    private void StartEdit(KnowledgeBaseCategory category)
    {
        _editingId = category.Id;
        _editName = category.Name;
        _editComment = category.Comment ?? string.Empty;
        _editParentId = category.ParentId?.ToString() ?? string.Empty;
        _confirmDeleteId = null;
    }

    private async Task SaveEditAsync()
    {
        if (_editingId is not int categoryId || string.IsNullOrWhiteSpace(_editName))
        {
            return;
        }

        int? parentId = int.TryParse(_editParentId, out int parsed) ? parsed : null;

        // Garde-fou de dernier rang : la liste déroulante n'offre déjà pas les descendantes, mais
        // l'arbre a pu changer depuis l'ouverture de l'écran par quelqu'un d'autre.
        if (parentId is int candidate && CategoryTree.WouldCreateCycle(_categories, categoryId, candidate))
        {
            ToastService.Notify(new ToastMessage(ToastType.Warning,
                "Cette catégorie ne peut pas être rattachée à elle-même ni à l'une de ses sous-catégories."));
            return;
        }

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            KnowledgeBaseCategory? category = await db.Set<KnowledgeBaseCategory>()
                .FirstOrDefaultAsync(item => item.Id == categoryId);

            if (category is null)
            {
                return;
            }

            category.Name = _editName.Trim();
            category.Comment = string.IsNullOrWhiteSpace(_editComment) ? null : _editComment.Trim();
            category.ParentId = parentId;

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, Tr.T("Catégorie enregistrée.")));
            await LoadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            ToastService.Notify(new ToastMessage(ToastType.Danger, Tr.T("Échec de l'enregistrement : {0}", ex.Message)));
        }
    }

    /// <summary>
    /// Supprime une catégorie sans rien perdre : ses sous-catégories et ses articles sont
    /// rattachés à sa catégorie parente (ou laissés à la racine / sans catégorie si elle n'en a
    /// pas). Supprimer un rangement ne doit jamais supprimer ce qui était rangé dedans.
    /// </summary>
    private async Task DeleteAsync(KnowledgeBaseCategory category)
    {
        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            List<KnowledgeBaseCategory> children = await db.Set<KnowledgeBaseCategory>()
                .Where(item => item.ParentId == category.Id)
                .ToListAsync();

            foreach (KnowledgeBaseCategory child in children)
            {
                child.ParentId = category.ParentId;
            }

            List<KnowledgeBaseArticle> articles = await db.Set<KnowledgeBaseArticle>()
                .Where(article => article.CategoryId == category.Id)
                .ToListAsync();

            foreach (KnowledgeBaseArticle article in articles)
            {
                article.CategoryId = category.ParentId;
            }

            KnowledgeBaseCategory? tracked = await db.Set<KnowledgeBaseCategory>()
                .FirstOrDefaultAsync(item => item.Id == category.Id);

            if (tracked is not null)
            {
                db.Set<KnowledgeBaseCategory>().Remove(tracked);
            }

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, Tr.T("Catégorie supprimée.")));
            await LoadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            _confirmDeleteId = null;
            ToastService.Notify(new ToastMessage(ToastType.Danger, Tr.T("Échec de la suppression : {0}", ex.Message)));
        }
    }
}
