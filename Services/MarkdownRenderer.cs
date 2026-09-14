using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GlpiNg.Modules.KnowledgeBase.Services;

/// <summary>
/// Rend en HTML le Markdown d'un article.
///
/// Un article est rédigé par un utilisateur et lu par tous ceux qui accèdent à la base : c'est le
/// chemin classique d'une injection de script, et c'est pourquoi le rendu est verrouillé à deux
/// endroits plutôt qu'un.
///
/// 1. <c>DisableHtml()</c> : le HTML brut écrit dans le Markdown n'est pas interprété, il ressort
///    échappé. Un <c>&lt;script&gt;</c> ou un <c>&lt;img onerror=...&gt;</c> collé dans un article
///    s'affiche donc comme du texte. C'est ce qui évite d'avoir à assainir du HTML après coup —
///    exercice qu'on rate toujours un jour.
/// 2. Filtrage des URL : Markdig produit bien un lien pour <c>[texte](javascript:...)</c>, que le
///    point 1 ne couvre pas. Les schémas dangereux sont donc neutralisés sur l'arbre du document,
///    avant rendu — même liste que les liens externes de l'hôte (voir ExternalLink dans le README).
///    Agir sur l'arbre plutôt que par expression régulière sur le HTML produit évite de dépendre
///    de la façon dont Markdig écrit ses attributs.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Schémas d'URL jamais rendus, quelle que soit la casse. Même liste que celle des liens
    /// externes côté hôte : un lien d'article n'a pas de raison d'être plus permissif qu'un lien
    /// configuré par un administrateur.
    /// </summary>
    private static readonly string[] BlockedSchemes = ["javascript:", "data:", "vbscript:", "file:"];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        // Tableaux, listes de tâches, texte barré, notes de bas de page, liens automatiques :
        // tout ce qu'on attend d'une procédure technique un peu longue.
        .UseAdvancedExtensions()
        // Un retour à la ligne simple devient un <br>. Sans ça, un article tapé au fil de l'eau —
        // ce que fait tout le monde — s'afficherait en un seul pavé, les retours étant avalés par
        // le Markdown standard. C'est aussi ce qui fait qu'un texte brut existant reste rendu tel
        // qu'il a été écrit.
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    /// <summary>HTML prêt à afficher. Une entrée vide rend une chaîne vide, pas un paragraphe fantôme.</summary>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);

        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (IsBlocked(link.Url))
            {
                // Le lien est vidé plutôt que supprimé : son libellé reste lisible, et le lecteur
                // voit qu'il y avait là quelque chose — un lien qui disparaît sans trace est plus
                // déroutant qu'un lien inerte.
                link.Url = string.Empty;
            }
        }

        using StringWriter writer = new();
        HtmlRenderer renderer = new(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }

    /// <summary>
    /// Texte débarrassé de sa syntaxe Markdown, pour les endroits qui n'affichent pas de HTML :
    /// extrait de la liste des articles, export d'un rapport. Approximatif par construction — il
    /// s'agit de rendre une ligne lisible, pas de reconstituer le texte d'origine.
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        System.Text.StringBuilder builder = new();

        foreach (LiteralInline literal in document.Descendants<LiteralInline>())
        {
            builder.Append(literal.Content.ToString()).Append(' ');
        }

        return builder.ToString().Trim();
    }

    private static bool IsBlocked(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.TrimStart();
        return BlockedSchemes.Any(scheme => trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));
    }
}
