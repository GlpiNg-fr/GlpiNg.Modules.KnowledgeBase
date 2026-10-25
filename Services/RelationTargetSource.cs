using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.KnowledgeBase.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.KnowledgeBase.Services;

/// <summary>
/// Articles comme cibles de rattachement. La visibilité de l'article (auteur, période, cibles) est
/// appliquée ici comme sur la liste : sans elle, le titre d'un article réservé à un groupe se lirait
/// depuis la fiche d'un ordinateur — voir <see cref="KnowledgeBaseService.IsVisible"/>.
/// </summary>
public sealed class RelationTargetSource(
    IDbContextFactory<DbContext> dbFactory,
    KnowledgeBaseService service,
    AuthenticationStateProvider authProvider) : IRelationTargetSource
{
    public IReadOnlyCollection<string> SupportedTypes { get; } = [ItemTypes.KnowledgeBaseArticle];

    public async Task<IReadOnlyList<RelationTarget>> SearchAsync(string itemType, string? term, int limit, CancellationToken cancellationToken = default)
    {
        string like = term?.Trim() ?? string.Empty;
        return await LoadAsync(a => a.Subject.Contains(like), limit, cancellationToken);
    }

    public async Task<IReadOnlyList<RelationTarget>> ResolveAsync(string itemType, IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default) =>
        await LoadAsync(a => ids.Contains(a.Id), int.MaxValue, cancellationToken);

    private async Task<IReadOnlyList<RelationTarget>> LoadAsync(System.Linq.Expressions.Expression<Func<KnowledgeBaseArticle, bool>> filter, int limit, CancellationToken ct)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Les cibles sont chargées : un article dont on n'a pas lu les cibles passerait pour ouvert à tous.
        List<KnowledgeBaseArticle> articles = await db.Set<KnowledgeBaseArticle>().AsNoTracking()
            .Include(a => a.Targets).Where(filter).OrderBy(a => a.Subject).ToListAsync(ct);

        PrincipalContext? viewer = null;
        AuthenticationState state = await authProvider.GetAuthenticationStateAsync();
        if (int.TryParse(state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out int userId))
        {
            viewer = await service.GetViewerAsync(userId, ct);
        }

        return [.. articles.Where(a => KnowledgeBaseService.IsVisible(a, viewer)).Take(limit)
            .Select(a => new RelationTarget(ItemTypes.KnowledgeBaseArticle, a.Id, a.Subject, $"/tools/knowledgebase/article/{a.Id}"))];
    }
}
