# CodeInsights pour Visual Studio

Lire dans Visual Studio 2022 et Visual Studio 2026 le dernier rapport CodeInsights
de la branche courante, et estimer l'effet des changements en cours avant le push.
L'extension consomme les rapports produits par la CI ; elle ne reproduit pas le
moteur du hub, et ne conclut jamais à l'absence de problème à partir d'une
analyse partielle.

La couche métier vit dans [`apps/core`](../core) — le même cœur que l'extension
VS Code et le serveur MCP. Visual Studio n'est qu'un adaptateur : un second
implémentation des transformations donnerait deux verdicts pour le même dépôt.

L'architecture est décrite dans [`docs/visualstudio-extension.md`](../../docs/visualstudio-extension.md).

## Installation

Le VSIX est produit par `.azuredevops/build-visualstudio-extension.yml` sur un
agent Windows. **Node.js 22+** doit être dans le `PATH` du poste : Visual Studio
n'embarque pas de runtime Node, et l'hôte TypeScript qui parle à `apps/core`
en a besoin.

Dans Visual Studio : **Extensions → Gérer les extensions → Installer depuis un
fichier VSIX**.

## Configuration

Les réglages sont dans **Outils → Options → CodeInsights**, avec les mêmes
clés que l'extension VS Code (`serverUrl`, `projectKey`, analyse locale,
couverture, duplication, dépendances).

1. Renseigner l'URL du hub.
2. Dans le hub : avatar → **Personal access tokens**. Un jeton de lecture
   suffit pour consulter ; ajouter la portée de triage pour traiter les issues.
   L'extension ne crée jamais de jeton elle-même.
3. **CodeInsights : se connecter**, coller le jeton. Il est chiffré par DPAPI
   pour l'utilisateur Windows courant — jamais dans les options synchronisées.
4. La résolution de projet est identique à VS Code : réglage du dossier, puis
   `.codeinsights/vscode.json` (le même fichier, lu aussi par le serveur MCP),
   puis l'URL de clone. Une ambiguïté est signalée plutôt que tranchée.

## Fonctionnalités

| Surface VS Code | Surface Visual Studio |
| --- | --- |
| Panneau CodeInsights | Fenêtre d'outils **CodeInsights** (Rapport, Issues, Détail, Règles) |
| Panneau Problèmes | Liste d'erreurs, source `CodeInsights` |
| CodeLens de fichier | CodeLens sur types et méthodes |
| Gouttière de couverture | CodeLens et liste d'erreurs ; pas de gouttière propriétaire |
| `@codeinsights` chat | Copier le prompt ; coller dans Copilot Chat |
| Fournisseur MCP de l'éditeur | Commande **configurer Copilot (MCP)** → `.mcp.json` utilisateur |

L'analyse locale, l'estimation de gate (jamais un « pass » local), le cache
daté hors ligne, le crochet pre-push sur demande et l'honnêteté des mesures
absentes sont ceux du cœur.

## Développement

```bash
cd apps/core && npm ci && npm run check
cd apps/mcp && npm ci && npm run check && npm run build
cd apps/visualstudio && npm ci && npm run check
```

Le packaging VSIX se fait sur Windows, Visual Studio 2022 17.9+ ou 2026 avec
la charge de travail **Développement d'extensions Visual Studio** :

```powershell
dotnet build apps/visualstudio/vsix/CodeInsights.VisualStudio.csproj -c Release
```
