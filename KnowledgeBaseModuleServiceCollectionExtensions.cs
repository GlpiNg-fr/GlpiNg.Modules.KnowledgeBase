using GlpiNg.Modules.Abstractions.Menu;
using GlpiNg.Modules.Abstractions.Reports;
using GlpiNg.Modules.KnowledgeBase.Reports;
using GlpiNg.Modules.KnowledgeBase.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GlpiNg.Modules.KnowledgeBase;

/// <summary>
/// Point d'enregistrement du module Base de connaissances dans le conteneur DI de l'hôte, même
/// principe que <c>InventoryModuleServiceCollectionExtensions.AddInventoryModule</c>.
///
/// Ce module n'expose aucun contrôleur (pas de protocole, pas d'API) : il n'a donc pas d'appel à
/// <c>AddApplicationPart</c>. Ses pages Razor, elles, vivent dans une autre assembly que l'hôte et
/// doivent être déclarées au routeur — voir <c>AdditionalAssemblies</c> dans Routes.razor.
///
/// Il dépend du <c>DbContext</c> de base : à appeler après que l'hôte a enregistré son DbContext
/// concret, comme les autres modules (voir Program.cs).
/// </summary>
public static class KnowledgeBaseModuleServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeBaseModule(this IServiceCollection services)
    {
        // Règles d'écriture et de lecture de la base : révisions, cibles, visibilité, compteur de
        // consultations. Scoped, comme tout ce qui lit la base sous le cloisonnement de
        // l'utilisateur courant.
        services.AddScoped<KnowledgeBaseService>();

        // Mémoire des consultations déjà comptées : partagée par toute l'application, sans quoi
        // elle ne servirait à rien — voir sa doc.
        services.AddSingleton<KnowledgeBaseViewTracker>();

        // Contribution du module au menu latéral de l'hôte (entrée « Base de connaissances » du
        // groupe « Outils ») — voir KnowledgeBaseMenuProvider.
        services.AddSingleton<IMenuProvider, KnowledgeBaseMenuProvider>();

        // Contribution du module aux rapports de l'hôte (/tools/reports) — voir
        // KnowledgeBaseReportProvider.
        services.AddScoped<IReportProvider, KnowledgeBaseReportProvider>();

        return services;
    }
}
