using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Entities;

namespace GlpiNg.Modules.KnowledgeBase.Models;

/// <summary>
/// Article de la base de connaissances, équivalent de <c>glpi_knowbaseitems</c> : un sujet, une
/// réponse, une catégorie.
///
/// <see cref="Content"/> est du <b>Markdown</b> et non du HTML : un article est lu par tous ceux
/// qui ont accès à la base, et stocker du HTML saisi par un utilisateur obligerait à l'assainir
/// avant chaque affichage — exercice qu'on rate toujours un jour. Le Markdown est rendu à
/// l'affichage par <c>MarkdownRenderer</c>, qui n'interprète jamais le HTML brut et neutralise les
/// schémas d'URL dangereux, et il est saisi depuis la barre d'outils de la fiche.
///
/// Conséquence heureuse : ce qui est stocké reste du texte, donc lisible, cherchable et
/// exportable tel quel — la recherche de la liste porte sur cette chaîne, pas sur un rendu.
/// </summary>
public class KnowledgeBaseArticle : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    /// <summary>Titre de l'article (« Sujet » dans GLPI).</summary>
    public required string Subject { get; set; }

    /// <summary>Corps de l'article (« Contenu » dans GLPI) — Markdown, voir la doc de la classe.</summary>
    public required string Content { get; set; }

    public int? CategoryId { get; set; }
    public KnowledgeBaseCategory? Category { get; set; }

    /// <summary>
    /// Article publié dans la FAQ : dans GLPI, c'est ce qui le rend lisible depuis l'interface
    /// simplifiée, en plus de la base de connaissances interne. GlpiNg n'a pas encore d'interface
    /// simplifiée (voir « Écarts avec GLPI » dans le README) : le drapeau sert ici de filtre et de
    /// marquage éditorial — « ceci est destiné aux utilisateurs, pas aux seuls techniciens ».
    /// </summary>
    public bool IsFaq { get; set; }

    /// <summary>Article épinglé : remonté en tête de liste, quel que soit le tri courant.</summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Nombre de consultations. Incrémenté à chaque ouverture de la fiche par un autre compte que
    /// l'auteur du dernier incrément immédiat — voir KnowledgeBaseService.RegisterViewAsync, qui
    /// explique pourquoi un simple ++ à chaque rendu ne convenait pas.
    /// </summary>
    public int ViewCount { get; set; }

    /// <summary>Compte ayant créé l'article, s'il existe encore ; le nom est conservé à part.</summary>
    public int? AuthorUserId { get; set; }

    /// <summary>
    /// Nom de l'auteur au moment de la rédaction. Dupliqué plutôt que résolu à l'affichage : un
    /// article survit au compte qui l'a écrit, et « article de (compte supprimé) » est une perte
    /// d'information gratuite dans une base qu'on consulte des années plus tard.
    /// </summary>
    public string? AuthorName { get; set; }

    /// <summary>Nom du dernier compte ayant modifié l'article — même raison que <see cref="AuthorName"/>.</summary>
    public string? LastEditorName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Id de l'article dans la base GLPI source (<c>glpi_knowbaseitems.id</c>), quand il vient de
    /// l'import — même rôle que sur la catégorie : rendre l'import idempotent.
    /// </summary>
    public int? SourceGlpiId { get; set; }

    public List<KnowledgeBaseArticleRevision> Revisions { get; set; } = [];

    public List<KnowledgeBaseArticleTarget> Targets { get; set; } = [];
}

/// <summary>
/// État antérieur d'un article, écrit à chaque enregistrement qui change le sujet ou le contenu
/// (voir KnowledgeBaseService.SaveAsync). Reprend les « révisions » de GLPI : une base de
/// connaissances se corrige à plusieurs mains, et retrouver ce qu'un article disait avant une
/// modification malheureuse est le seul recours quand la bonne réponse a été effacée.
/// </summary>
public class KnowledgeBaseArticleRevision
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public KnowledgeBaseArticle? Article { get; set; }

    /// <summary>Numéro de révision, croissant à partir de 1 pour chaque article.</summary>
    public int Number { get; set; }

    public required string Subject { get; set; }
    public required string Content { get; set; }

    /// <summary>Compte à l'origine de la modification qui a produit cette révision.</summary>
    public string? EditorName { get; set; }

    /// <summary>Date de la modification qui a archivé cet état.</summary>
    public DateTime RevisedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cible de visibilité d'un article : une entité, un groupe, un profil ou un utilisateur autorisé
/// à le lire. Un article sans aucune cible est visible de tous ceux qui voient la base (dans les
/// limites du cloisonnement par entité, qui s'applique de toute façon) — c'est le cas courant, et
/// c'est le défaut de GLPI.
///
/// Référence polymorphe non contrainte (pas de FK), comme <c>DeploymentPackageTarget</c> et comme
/// GLPI lui-même : les quatre types visés vivent chez l'hôte, que ce module ne référence pas. Les
/// noms affichés sont résolus via <see cref="IPrincipalDirectory"/>, l'évaluation côté lecteur via
/// <see cref="IPrincipalContextProvider"/>.
/// </summary>
public class KnowledgeBaseArticleTarget
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public KnowledgeBaseArticle? Article { get; set; }

    public PrincipalKind Type { get; set; }
    public int ItemId { get; set; }
}
