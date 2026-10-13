---
title: Extension Visual Studio CodeInsights
description: Adaptateur Visual Studio 2022/2026 du cœur partagé CodeInsights — rapport de branche, issues, analyse locale.
---

# Extension Visual Studio CodeInsights

## Objet et statut

Cette page décrit l'extension Visual Studio `CodeInsights.VisualStudio`
hébergée dans [apps/visualstudio](../apps/visualstudio/). Elle offre, dans
Visual Studio 2022 (17.9+) et Visual Studio 2026, les mêmes capacités métier
que l'[extension VS Code](vscode-extension.md) : dernier rapport de la branche,
issues recalées, traitement, analyse locale estimée, couverture, duplication,
dépendances et catalogue de règles.

Ce n'est **pas** un second moteur. Depuis le lot A du
[serveur MCP](mcp-server.md), la couche métier vit dans `apps/core`. Visual
Studio lance un hôte Node qui importe ce cœur, comme VS Code l'importe en
processus et comme le serveur MCP l'importe en stdio. Deux implémentations
C# des transformations donneraient deux verdicts pour le même dépôt.

Les lots fonctionnels L2 à L9 de VS Code sont repris ici. Le packaging VSIX
se fait sur un agent Windows ; les checks de l'hôte tournent partout.

## Architecture

```text
apps/core/                 -> transformations partagées (API, rapport, local, drift, gate)
apps/visualstudio/
  src/                     -> hôte JSON-RPC stdio (Node 22), ports git.exe comme apps/mcp
  vsix/                    -> adaptateur VisualStudio.Extensibility (net8.0-windows, hors processus)
```

L'hôte parle JSON-RPC 2.0 avec framing `Content-Length`, le format par défaut
de StreamJsonRpc. stdout est réservé au protocole. Le jeton personnel n'y
circule que dans `initialize` / `setToken` ; il n'est jamais journalisé.

Côté IDE, [VisualStudio.Extensibility](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/)
est le modèle recommandé pour VS 2022+ : hors processus, .NET 8, installation
sans redémarrage. Un VSIX ciblant l'API 17.x s'exécute aussi dans Visual Studio
2026 ([compatibilité](https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/extension-compatibility)).

```text
vsix/
  Extension.cs             -> DI, métadonnées VSIX
  Settings/                -> Outils → Options → CodeInsights
  Auth/TokenStore.cs       -> PAT chiffré DPAPI (utilisateur courant)
  Host/HostClient.cs       -> processus Node + StreamJsonRpc
  Runtime/                 -> cwd, rafraîchissement, relais des commandes
  ToolWindows/             -> Rapport, Issues, Détail, Règles
  Editor/                  -> liste d'erreurs, CodeLens
  Commands/                -> palette Outils / Extensions
```

Le VSIX embarque `dist/host.mjs` et, lorsqu'il a été construit, le serveur MCP
`mcp-server.mjs` pour Copilot.

## Correspondance des surfaces

| VS Code | Visual Studio |
| --- | --- |
| SecretStorage | DPAPI `%LOCALAPPDATA%/CodeInsights/visualstudio/token.bin` |
| Panneau du bas | Fenêtre d'outils **CodeInsights** |
| Panneau Problèmes | Liste d'erreurs via `Languages().GetDiagnosticsReporter` |
| CodeLens fichier | CodeLens sur types et méthodes |
| Gouttière couverture / duplication | CodeLens ; une gouttière propriétaire n'existe pas encore dans le SDK hors processus |
| Chat `@codeinsights` | Copier le prompt, à coller dans Copilot Chat |
| `mcpServerDefinitionProviders` | Commande **configurer Copilot (MCP)** → `~/.mcp.json` |
| Git de l'éditeur | `git` du PATH, comme le serveur MCP |

Les réglages portent les mêmes noms que `codeinsights.*` de VS Code. Le fichier
`.codeinsights/vscode.json` est lu tel quel : VS Code, Visual Studio et l'agent
MCP doivent nommer le même projet pour le même dossier.

Le rapport suit la même règle que VS Code : dernier build de branche, à défaut dernier
build de pull request issu de la branche, nommé `PR #N · source → cible` dans le
statut, la notification et l'arbre ; le lien vers le hub sélectionne cette PR
([extension VS Code](vscode-extension.md#mvp--lire-le-dernier-rapport-de-ma-branche)).

**Node.js 22+** est requis dans le PATH du poste. VS Code fournit un Node ;
Visual Studio non. Un producteur local absent reste déclaré absent.

## Décisions

1. **Même cœur, pas de port C# des transformations.** L'estimation de gate, le
   report de dérive, le groupement des issues et l'honnêteté des mesures
   absentes restent dans `apps/core`.
2. **VisualStudio.Extensibility hors processus**, pas le VSSDK in-proc. Les
   diagnostics et la fenêtre d'outils suffisent pour le périmètre ; un besoin
   d'API encore absente se traite en hybride plus tard, pas en dupliquant le
   métier.
3. **Distribution interne VSIX**, comme l'extension VS Code. Cible d'installation
   `[17.0,)` : Visual Studio 2022 et 2026.
4. **Le chat natif VS Code n'est pas reproduit.** Copilot Chat dans Visual
   Studio n'expose pas le participant `@codeinsights` ; le prompt du hub ou
   le prompt local annoncé comme tel se copie.
5. **Le jeton ne va pas dans les Options.** Les options Visual Studio peuvent
   être exportées ; DPAPI limite le secret à l'utilisateur Windows courant.

## Validation

- `npm run check` dans `apps/visualstudio` : `tsc --noEmit`, bundle, framing
  JSON-RPC, dialogue réel avec `dist/host.mjs`, snapshot sans URL (jamais un
  gate vert), versions `package.json` / csproj alignées.
- Packaging : `dotnet build apps/visualstudio/vsix/CodeInsights.VisualStudio.csproj -c Release` sur Windows.
- Les checks de `apps/core` restent le filet des transformations.

## Risques

Les mêmes que VS Code (divergence d'estimation, dérive des positions, fuite
de secret), plus : un poste sans Node voit l'extension inactive, clairement
annoncée, plutôt qu'un rapport vide pris pour un succès.
