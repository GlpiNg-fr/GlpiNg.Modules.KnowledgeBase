# GlpiNg.Modules.KnowledgeBase

*[English version](README.en.md)*

Module Base de connaissances de GlpiNg.

> **Avertissement** — GlpiNg est un projet indépendant. Il n'est ni affilié à, ni approuvé,
> soutenu ou sponsorisé par Teclib' ou le projet GLPI. « GLPI » et « GLPI-Agent » sont des
> marques de leurs propriétaires respectifs ; elles ne sont citées ici que pour décrire la
> compatibilité de GlpiNg avec le protocole GLPI-Agent et l'import depuis une base GLPI.

## Contenu

- Articles en Markdown, rendus sans HTML brut (Markdig)
- Catégories, révisions et historique des modifications
- Cibles de visibilité (entités, groupes, profils, utilisateurs) et période de publication
- Compteur de consultations et rapport

## Utilisation

Ce dépôt est un sous-module de [GlpiNg](https://github.com/GlpiNg-fr/GlpiNg), sous
`src/GlpiNg.Modules.KnowledgeBase`. Il ne se compile pas seul : il référence `GlpiNg.Modules.Abstractions` par chemin relatif.

```bash
git clone --recurse-submodules https://github.com/GlpiNg-fr/GlpiNg.git
```

L'hôte l'enregistre par `services.AddKnowledgeBaseModule()` (voir `Program.cs`).

## Licence

[GNU Affero General Public License v3.0](LICENSE).
