using GlpiNg.Modules.Abstractions.Entities;

namespace GlpiNg.Modules.KnowledgeBase.Models;

/// <summary>
/// Catégorie d'articles, équivalent de <c>glpi_knowbaseitemcategories</c> : une arborescence, pas
/// une liste plate — une base de connaissances se range par domaine puis par sous-domaine
/// (« Réseau &gt; VPN »), et la retrouver par navigation vaut souvent mieux que par recherche.
///
/// La profondeur n'est pas bornée en base ; l'écran affiche l'arbre entier
/// (voir Components/Pages/KnowledgeBase/Categories.razor).
/// </summary>
public class KnowledgeBaseCategory : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Comment { get; set; }

    /// <summary>Catégorie parente, <c>null</c> pour une racine.</summary>
    public int? ParentId { get; set; }
    public KnowledgeBaseCategory? Parent { get; set; }

    public List<KnowledgeBaseCategory> Children { get; set; } = [];

    public List<KnowledgeBaseArticle> Articles { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Id de la catégorie dans la base GLPI source (<c>glpi_knowbaseitemcategories.id</c>), quand
    /// elle vient de l'import — c'est ce qui le rend idempotent : un second passage met à jour au
    /// lieu de recréer l'arbre à côté. Même principe que <c>Computer.SourceGlpiId</c>.
    /// </summary>
    public int? SourceGlpiId { get; set; }
}
