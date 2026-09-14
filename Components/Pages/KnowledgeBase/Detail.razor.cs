using BlazorBootstrap;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.KnowledgeBase.Models;
using GlpiNg.Modules.KnowledgeBase.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.KnowledgeBase.Components.Pages.KnowledgeBase;

/// <summary>
/// Fiche d'un article : lecture, édition, cibles de visibilité et révisions. Sert aussi de
/// formulaire de création (route <c>/tools/knowledgebase/new</c>) — c'est le même écran, ouvert
/// directement en édition sur un article vide, plutôt qu'une modale qui ne montrerait pas ce que
/// l'article deviendra.
/// </summary>
public partial class Detail : ComponentBase
{
    [Parameter]
    public int? ArticleId { get; set; }

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private KnowledgeBaseService Service { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    [Inject]
    private NavigationManager Nav { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthStateTask { get; set; }

    private KnowledgeBaseArticle? _article;

    /// <summary>Copie d'avant édition, pour que « Annuler » rende vraiment l'article d'origine.</summary>
    private KnowledgeBaseArticle? _beforeEdit;

    private List<(KnowledgeBaseCategory Category, int Depth)> _categoryTree = [];
    private List<KnowledgeBaseArticleRevision> _revisions = [];

    /// <summary>Noms des cibles, par (type, identifiant) : l'article ne stocke que des identifiants.</summary>
    private readonly Dictionary<(PrincipalKind Kind, int ItemId), string> _targetNames = [];

    private List<PrincipalOption> _targetOptions = [];
    private string _newTargetKind = nameof(PrincipalKind.Entity);
    private string _newTargetItemId = string.Empty;

    private string _activeTab = "article";
    private bool _editMode;
    private bool _isSaving;
    private bool _notFound;
    private bool _confirmDelete;
    private int? _openRevisionId;
    private int? _loadedId;

    /// <summary>Id DOM du textarea : c'est par lui que la barre d'outils travaille (voir glpi-ng.js).</summary>
    private const string ContentEditorId = "kb-content";

    private bool _editorPreview;

    /// <summary>Sélection à rétablir après le prochain rendu, quand une commande a réécrit le texte.</summary>
    private (int Start, int End)? _pendingSelection;

    /// <summary>
    /// Une entrée de la barre d'outils. Deux familles : celles qui encadrent la sélection
    /// (<c>Before</c>/<c>After</c>) et celles qui préfixent des lignes entières
    /// (<c>LinePrefix</c> — titres, listes, citations), que la barre applique en bascule.
    /// </summary>
    private sealed record EditorCommand(
        string Label,
        string Icon,
        string? Before = null,
        string? After = null,
        string? Placeholder = null,
        string? LinePrefix = null)
    {
        public bool IsSeparator => Icon.Length == 0;

        public static EditorCommand Separator => new(string.Empty, string.Empty);
    }

    private static readonly EditorCommand[] EditorCommands =
    [
        new("Gras", "ti-bold", "**", "**", "texte en gras"),
        new("Italique", "ti-italic", "*", "*", "texte en italique"),
        new("Barré", "ti-strikethrough", "~~", "~~", "texte barré"),
        EditorCommand.Separator,
        new("Titre", "ti-heading", LinePrefix: "## "),
        new("Liste à puces", "ti-list", LinePrefix: "- "),
        new("Liste numérotée", "ti-list-numbers", LinePrefix: "1. "),
        new("Citation", "ti-quote", LinePrefix: "> "),
        new("Case à cocher", "ti-checkbox", LinePrefix: "- [ ] "),
        EditorCommand.Separator,
        new("Code en ligne", "ti-code", "`", "`", "code"),
        new("Bloc de code", "ti-source-code", "```\n", "\n```", "collez votre commande ici"),
        new("Lien", "ti-link", "[", "](https://)", "libellé du lien"),
        new("Image", "ti-photo", "![", "](https://)", "texte alternatif"),
        new("Tableau", "ti-table", "| Colonne A | Colonne B |\n| --- | --- |\n| valeur | valeur |\n"),
        new("Trait de séparation", "ti-minus", "\n---\n"),
    ];

    /// <summary>Ce que renvoie <c>glpiNg.editorCommand</c> : le texte réécrit et la sélection à rétablir.</summary>
    private sealed record EditorResult(string Value, int Start, int End);

    /// <summary>
    /// Applique une commande de la barre d'outils. Le texte est réécrit côté JS (seul endroit d'où
    /// l'on voit le curseur et la sélection), puis réaffecté au modèle ici : c'est le C# qui reste
    /// maître de la valeur enregistrée.
    /// </summary>
    private async Task ApplyCommandAsync(EditorCommand command)
    {
        if (_article is null || command.IsSeparator)
        {
            return;
        }

        EditorResult? result = await JS.InvokeAsync<EditorResult?>("glpiNg.editorCommand", ContentEditorId, new
        {
            before = command.Before,
            after = command.After,
            placeholder = command.Placeholder,
            linePrefix = command.LinePrefix,
        });

        if (result is null)
        {
            return;
        }

        _article.Content = result.Value;
        _pendingSelection = (result.Start, result.End);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // La sélection ne peut être rétablie qu'une fois Blazor a réécrit la valeur du textarea :
        // la poser avant serait effacé par le rendu suivant.
        if (_pendingSelection is not ({ } start, { } end))
        {
            return;
        }

        _pendingSelection = null;
        await JS.InvokeVoidAsync("glpiNg.setEditorSelection", ContentEditorId, start, end);
    }

    private bool IsNew => _article is not null && _article.Id == 0;

    private bool CanSave => _article is not null
        && !string.IsNullOrWhiteSpace(_article.Subject)
        && !string.IsNullOrWhiteSpace(_article.Content);

    private IEnumerable<(string Key, string Icon, string Label, int? Count)> Tabs
    {
        get
        {
            yield return ("article", "ti-book", "Article", null);

            // Les cibles et les révisions n'existent que pour un article enregistré : les proposer
            // à la création laisserait croire qu'on peut les saisir avant qu'il y ait un article à
            // restreindre ou un état à archiver.
            if (!IsNew)
            {
                yield return ("targets", "ti-lock", "Cibles", _article?.Targets.Count is > 0 ? _article.Targets.Count : null);
                yield return ("revisions", "ti-history", "Révisions", _revisions.Count > 0 ? _revisions.Count : null);
            }
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedId == ArticleId && _article is not null)
        {
            return;
        }

        _loadedId = ArticleId;
        _activeTab = "article";
        _confirmDelete = false;
        _openRevisionId = null;

        await LoadCategoriesAsync();

        if (ArticleId is not int articleId)
        {
            _notFound = false;
            _editMode = true;
            _article = new KnowledgeBaseArticle
            {
                Subject = string.Empty,
                Content = string.Empty,
                AuthorUserId = await CurrentUserIdAsync(),
            };
            _revisions = [];
            return;
        }

        await LoadArticleAsync(articleId);
    }

    private async Task LoadArticleAsync(int articleId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        KnowledgeBaseArticle? article = await db.Set<KnowledgeBaseArticle>()
            .AsNoTracking()
            .Include(item => item.Category)
            .Include(item => item.Targets)
            .FirstOrDefaultAsync(item => item.Id == articleId);

        PrincipalContext? viewer = await CurrentUserIdAsync() is int userId
            ? await Service.GetViewerAsync(userId)
            : null;

        // La visibilité est vérifiée ici comme dans la liste : sans ce contrôle, une URL collée
        // ouvrirait un article que son ciblage réserve à d'autres.
        if (article is null || !KnowledgeBaseService.IsVisible(article, viewer))
        {
            _notFound = true;
            _article = null;
            return;
        }

        _notFound = false;
        _editMode = false;
        _article = article;

        _revisions = await db.Set<KnowledgeBaseArticleRevision>()
            .AsNoTracking()
            .Where(revision => revision.ArticleId == articleId)
            .OrderByDescending(revision => revision.Number)
            .ToListAsync();

        await LoadTargetNamesAsync();
        await LoadTargetOptionsAsync();

        if (await CurrentUserIdAsync() is int readerId)
        {
            await Service.RegisterViewAsync(articleId, readerId);
        }
    }

    private async Task LoadCategoriesAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        List<KnowledgeBaseCategory> categories = await db.Set<KnowledgeBaseCategory>()
            .AsNoTracking()
            .ToListAsync();

        _categoryTree = CategoryTree.Flatten(categories);
    }

    /// <summary>
    /// Résout le nom affiché de chaque cible. Les quatre annuaires sont interrogés d'un coup
    /// plutôt qu'un appel par cible : quatre requêtes au total, quel que soit le nombre de cibles.
    /// </summary>
    private async Task LoadTargetNamesAsync()
    {
        _targetNames.Clear();

        if (_article is null || _article.Targets.Count == 0)
        {
            return;
        }

        foreach (PrincipalKind kind in _article.Targets.Select(target => target.Type).Distinct())
        {
            foreach (PrincipalOption option in await Directory.GetAsync(kind))
            {
                _targetNames[(kind, option.Id)] = option.Name;
            }
        }
    }

    private async Task LoadTargetOptionsAsync()
        => _targetOptions = [.. await Directory.GetAsync(Enum.Parse<PrincipalKind>(_newTargetKind))];

    private async Task SetTargetKindAsync(string? kind)
    {
        _newTargetKind = kind ?? nameof(PrincipalKind.Entity);
        _newTargetItemId = string.Empty;
        await LoadTargetOptionsAsync();
    }

    private void SetCategory(string? value)
    {
        if (_article is null)
        {
            return;
        }

        _article.CategoryId = int.TryParse(value, out int categoryId) ? categoryId : null;
    }

    private void EnterEditMode()
    {
        if (_article is null)
        {
            return;
        }

        _beforeEdit = Clone(_article);
        _editMode = true;
    }

    private void CancelEdit()
    {
        if (IsNew)
        {
            Nav.NavigateTo("/tools/knowledgebase");
            return;
        }

        if (_beforeEdit is not null)
        {
            _article = _beforeEdit;
            _beforeEdit = null;
        }

        _editMode = false;
    }

    private async Task SaveAsync()
    {
        if (_article is null || !CanSave)
        {
            return;
        }

        _isSaving = true;

        try
        {
            bool wasNew = IsNew;
            int id = await Service.SaveAsync(_article, await CurrentUserNameAsync());

            ToastService.Notify(new ToastMessage(ToastType.Success,
                wasNew ? "Article créé." : "Article enregistré."));

            if (wasNew)
            {
                Nav.NavigateTo($"/tools/knowledgebase/article/{id}");
                return;
            }

            _editMode = false;
            _beforeEdit = null;
            await LoadArticleAsync(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            // Typiquement un droit d'écriture refusé par le DbContext de l'hôte sur la section
            // « Outils » : le message brut est plus utile qu'un échec muet.
            ToastService.Notify(new ToastMessage(ToastType.Danger, $"Échec de l'enregistrement : {ex.Message}"));
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_article is null || IsNew)
        {
            return;
        }

        try
        {
            await Service.DeleteArticleAsync(_article.Id);
            ToastService.Notify(new ToastMessage(ToastType.Success, "Article supprimé."));
            Nav.NavigateTo("/tools/knowledgebase");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            _confirmDelete = false;
            ToastService.Notify(new ToastMessage(ToastType.Danger, $"Échec de la suppression : {ex.Message}"));
        }
    }

    private async Task AddTargetAsync()
    {
        if (_article is null || IsNew || !int.TryParse(_newTargetItemId, out int itemId))
        {
            return;
        }

        PrincipalKind kind = Enum.Parse<PrincipalKind>(_newTargetKind);

        if (_article.Targets.Any(target => target.Type == kind && target.ItemId == itemId))
        {
            return;
        }

        List<(PrincipalKind, int)> targets = [.. _article.Targets.Select(target => (target.Type, target.ItemId)), (kind, itemId)];

        await ApplyTargetsAsync(targets);
        _newTargetItemId = string.Empty;
    }

    private async Task RemoveTargetAsync(KnowledgeBaseArticleTarget removed)
    {
        if (_article is null)
        {
            return;
        }

        List<(PrincipalKind, int)> targets = [.. _article.Targets
            .Where(target => target.Id != removed.Id)
            .Select(target => (target.Type, target.ItemId))];

        await ApplyTargetsAsync(targets);
    }

    private async Task ApplyTargetsAsync(IReadOnlyList<(PrincipalKind Kind, int ItemId)> targets)
    {
        if (_article is null)
        {
            return;
        }

        try
        {
            await Service.SetTargetsAsync(_article.Id, targets);
            await LoadArticleAsync(_article.Id);
            _activeTab = "targets";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            ToastService.Notify(new ToastMessage(ToastType.Danger, $"Échec de l'enregistrement des cibles : {ex.Message}"));
        }
    }

    private void ToggleRevision(int revisionId)
        => _openRevisionId = _openRevisionId == revisionId ? null : revisionId;

    /// <summary>
    /// Remet le sujet et le contenu d'une révision dans l'article. Passe par l'enregistrement
    /// normal : l'état actuel devient donc lui-même une révision, et une restauration faite par
    /// erreur se défait comme n'importe quelle modification.
    /// </summary>
    private async Task RestoreRevisionAsync(KnowledgeBaseArticleRevision revision)
    {
        if (_article is null)
        {
            return;
        }

        _article.Subject = revision.Subject;
        _article.Content = revision.Content;

        try
        {
            await Service.SaveAsync(_article, await CurrentUserNameAsync());
            ToastService.Notify(new ToastMessage(ToastType.Success, $"Révision n°{revision.Number} restaurée."));
            await LoadArticleAsync(_article.Id);
            _activeTab = "article";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            ToastService.Notify(new ToastMessage(ToastType.Danger, $"Échec de la restauration : {ex.Message}"));
        }
    }

    private string TargetName(KnowledgeBaseArticleTarget target)
        => _targetNames.TryGetValue((target.Type, target.ItemId), out string? name)
            ? name
            // Cible dont l'objet a disparu (groupe supprimé, compte effacé) : on montre
            // l'identifiant plutôt que rien, sinon la ligne devient impossible à comprendre — et
            // donc à retirer en connaissance de cause.
            : $"#{target.ItemId} (introuvable)";

    private static string KindLabel(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Entity => "Entité",
        PrincipalKind.Group => "Groupe",
        PrincipalKind.Profile => "Profil",
        PrincipalKind.User => "Utilisateur",
        _ => kind.ToString(),
    };

    private async Task<int?> CurrentUserIdAsync()
    {
        if (AuthStateTask is null)
        {
            return null;
        }

        AuthenticationState authState = await AuthStateTask;
        string? claim = authState.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return int.TryParse(claim, out int userId) ? userId : null;
    }

    private async Task<string> CurrentUserNameAsync()
    {
        if (AuthStateTask is null)
        {
            return "Système";
        }

        AuthenticationState authState = await AuthStateTask;
        return authState.User.Identity?.Name is { Length: > 0 } name ? name : "Système";
    }

    private static KnowledgeBaseArticle Clone(KnowledgeBaseArticle article) => new()
    {
        Id = article.Id,
        Subject = article.Subject,
        Content = article.Content,
        CategoryId = article.CategoryId,
        Category = article.Category,
        IsFaq = article.IsFaq,
        IsPinned = article.IsPinned,
        ViewCount = article.ViewCount,
        AuthorUserId = article.AuthorUserId,
        AuthorName = article.AuthorName,
        LastEditorName = article.LastEditorName,
        CreatedAt = article.CreatedAt,
        UpdatedAt = article.UpdatedAt,
        EntityId = article.EntityId,
        IsRecursive = article.IsRecursive,
        Targets = article.Targets,
    };
}
