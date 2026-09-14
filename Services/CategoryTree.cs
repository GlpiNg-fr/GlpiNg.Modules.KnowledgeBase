using GlpiNg.Modules.KnowledgeBase.Models;

namespace GlpiNg.Modules.KnowledgeBase.Services;

/// <summary>
/// Mise à plat de l'arbre des catégories, partagée par les trois écrans du module (liste, fiche,
/// gestion des catégories) : ils affichent tous le même arbre, et une seule règle d'orphelin vaut
/// mieux que trois.
/// </summary>
public static class CategoryTree
{
    /// <summary>
    /// Rend les catégories dans l'ordre d'affichage, avec leur profondeur.
    ///
    /// Une catégorie dont le parent est absent de la liste — cloisonnement par entité, ou parent
    /// supprimé — est traitée comme une racine plutôt que perdue : sans ça, toute une branche
    /// disparaîtrait de l'écran alors que ses articles, eux, restent visibles.
    /// </summary>
    public static List<(KnowledgeBaseCategory Category, int Depth)> Flatten(IReadOnlyList<KnowledgeBaseCategory> categories)
    {
        HashSet<int> known = [.. categories.Select(category => category.Id)];
        List<(KnowledgeBaseCategory Category, int Depth)> ordered = [];

        void Append(int? parentId, int depth)
        {
            IEnumerable<KnowledgeBaseCategory> level = categories
                .Where(category => category.ParentId == parentId
                    || (parentId is null && category.ParentId is int orphan && !known.Contains(orphan)))
                .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (KnowledgeBaseCategory category in level)
            {
                ordered.Add((category, depth));
                Append(category.Id, depth + 1);
            }
        }

        Append(null, 0);
        return ordered;
    }

    /// <summary>
    /// Identifiants d'une catégorie et de toutes ses descendantes. Sert à compter et à filtrer :
    /// sélectionner « Réseau » doit montrer les articles de « Réseau &gt; VPN », sans quoi une
    /// catégorie intermédiaire paraîtrait vide.
    /// </summary>
    public static HashSet<int> Branch(IReadOnlyList<KnowledgeBaseCategory> categories, int rootId)
    {
        HashSet<int> ids = [rootId];
        bool added = true;

        // Parcours par vagues plutôt que récursif : les catégories arrivent dans un ordre
        // quelconque, un enfant peut donc précéder son parent dans la liste.
        while (added)
        {
            added = false;

            foreach (KnowledgeBaseCategory category in categories)
            {
                if (category.ParentId is int parentId && ids.Contains(parentId) && ids.Add(category.Id))
                {
                    added = true;
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Vrai si <paramref name="candidateParentId"/> est la catégorie elle-même ou l'une de ses
    /// descendantes — donc un parent impossible : le rattacher là détacherait la branche de
    /// l'arbre et la rendrait inatteignable (voir l'écran des catégories).
    /// </summary>
    public static bool WouldCreateCycle(IReadOnlyList<KnowledgeBaseCategory> categories, int categoryId, int candidateParentId)
        => Branch(categories, categoryId).Contains(candidateParentId);
}
