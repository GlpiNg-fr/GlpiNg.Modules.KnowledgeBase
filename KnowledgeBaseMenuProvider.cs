using GlpiNg.Modules.Abstractions.Menu;

namespace GlpiNg.Modules.KnowledgeBase;

/// <summary>
/// Contribue l'entrée « Base de connaissances » du groupe « Outils », que l'hôte affichait
/// jusqu'ici désactivée faute de page derrière — même montage qu'<c>InventoryMenuProvider</c> et
/// <c>DeploymentMenuProvider</c>, dont les entrées du même groupe sont fusionnées avec celle-ci.
/// </summary>
public sealed class KnowledgeBaseMenuProvider : IMenuProvider
{
    public IReadOnlyList<MenuGroup> GetMenuGroups() =>
    [
        new("outils", "ti-briefcase", "Outils",
        [
            new("Base de connaissances", "/tools/knowledgebase", "ti-book"),
        ]),
    ];
}
