<!-- FOR AI AGENTS - Human readability is a side effect, not a goal -->
<!-- Last verified: 2026-09-05 against apps/api, apps/web, docker-compose.yml, Dockerfile, docs/*.md -->

# AGENTS.md — Up.France.CodeInsights

**Portée** : tout le dépôt Up.France.CodeInsights. Planifier avec une todo list, déléguer les recherches indépendantes lorsque pertinent, vérifier les changements et documenter chaque évolution dans `docs/`. Garder les modifications minimales ; aucune nouvelle abstraction ou dépendance sans besoin. Pour Terraform, conserver les conventions Azure existantes et ne pas appliquer de changement cloud sans autorisation. Utilise toujours les `Best practices` de chaque stack.

## Vue d'ensemble

ASP.NET Core 10 + EF Core/Npgsql + PostgreSQL en backend, React (Vite/TS) en frontend, packagés dans **une seule image** ([Dockerfile](Dockerfile)) qui build le SPA puis sert les statiques depuis ASP.NET Core. Doc faisant autorité : [README.md](README.md) · [architecture](docs/architecture.md) · [exploitation](docs/exploitation.md) · [intégration](docs/integration.md) · [performances](docs/performances.md) · [roadmap](docs/roadmap.md).

## Commands (verified)

| Task                      | Command                                                     | Notes                                                                                                                                                                                                                |
| ------------------------- | ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Build image (canonique)   | `docker build -t codeinsights:local .`                      | depuis la racine ; embarque web + API ensemble                                                                                                                                                                       |
| Valider la config compose | `docker compose config --quiet`                             | depuis la racine                                                                                                                                                                                                     |
| Tests API                 | `dotnet test tests/CodeInsights.Tests -c Release`           | xUnit v3 sur Microsoft Testing Platform. Ajouter `CODEINSIGHTS_TEST_DB` (PostgreSQL jetable, rôle `CREATEDB`) et `CODEINSIGHTS_TEST_URL` (API locale) pour les tests complets ; jamais une base de production.      |
| Checks frontend           | `npm ci && npm run check` dans `apps/web`                   | TypeScript puis 22 tests Vitest en TypeScript ; `npm run check:ci` produit JUnit, LCOV et Cobertura pour Azure Pipelines.                                                                                            |
| Checks cœur partagé       | `npm ci && npm run check` dans `apps/core`                  | `tsc --noEmit` puis les 26 assertions `src/*.check.mjs` ; à lancer avant les checks de `apps/vscode`, `apps/visualstudio` et `apps/mcp`, qui en dépendent.                                                            |
| Checks extension VS Code  | `npm ci && npm run check` dans `apps/vscode`                | `tsc --noEmit` puis `src/severity-colors.check.mjs` ; `npm run package` produit le VSIX.                                                                                                                             |
| Checks serveur MCP        | `npm ci && npm run check` dans `apps/mcp`                   | `tsc --noEmit`, assertions, bundle, puis dialogue MCP réel avec `dist/server.js` ; `npm run build` produit le bundle. Son `npm ci` est un prérequis du build de `apps/vscode`, qui embarque ce serveur dans le VSIX. |
| Checks extension VS       | `npm ci && npm run check` dans `apps/visualstudio`          | `tsc --noEmit`, bundle de l'hôte Node, JSON-RPC réel avec `dist/host.mjs`. Le VSIX se construit sur Windows : `dotnet build apps/visualstudio/vsix/CodeInsights.VisualStudio.csproj -c Release`. |
| Démarrage local           | `docker compose up -d`                                      | React et API sur 8000, PostgreSQL privé ; variables requises dans [exploitation.md](docs/exploitation.md)                                                                                                            |

## Conventions .NET

- Solution : `CodeInsights.slnx`, excluant les échantillons `src/`.
- Un seul `Directory.Build.props` centralise framework, nullable et versions de packages.
- Utiliser le skill `copilot-orchestrator:up-csharp-developer`, DI native, `IOptions<T>`, I/O asynchrones et `CancellationToken`.
- Utiliser des DTO explicites, PasswordHasher et JWT ; ne pas exposer les entités EF.
- Build : `dotnet build CodeInsights.slnx -c Release`. Les tests API utilisent xUnit v3 et s'exécutent avec `dotnet test tests/CodeInsights.Tests -c Release`.
- Migrations : `dotnet run --project apps/api -- --migrate`, migrations EF Core et verrou natif, avant toute ouverture HTTP.
- Healthcheck image : `/api/v1/health`.

## File Map

```text
apps/api/                -> API .NET 10 (Endpoints/, Data/, Auth/, Services/, Program.cs)
apps/web/                 -> frontend React (Vite/TS), manifeste npm et sources
apps/core/                -> cœur partagé TS sans dépendance à vscode (api/, report/, local/, context/, hub/) et ses assertions
apps/vscode/              -> extension VS Code (TS/esbuild), plan dans docs/vscode-extension.md
apps/visualstudio/        -> extension Visual Studio 2022/2026 (hôte Node + VSIX), plan dans docs/visualstudio-extension.md
apps/mcp/                 -> serveur MCP stdio (TS/esbuild), plan dans docs/mcp-server.md
infra/          -> infrastructure Terraform (PLATFORM_SAST.md)
docs/                      -> documentation faisant autorité — la lire avant de changer un comportement
```

> Pipelines de référence : `.azuredevops/deploy-apps.yml` (plateforme),
> `build-vscode-extension.yml` (VSIX, cœur et serveur MCP embarqué),
> `build-visualstudio-extension.yml` (VSIX Visual Studio, hôte Node et serveur MCP embarqué),
> `build-mcp-server.yml` (paquet npm `@upcoop/codeinsights-mcp`).

## Configuration

- La connexion utilise `ConnectionStrings__CodeInsights` ; les options sont regroupées dans `Authentication`, `Bootstrap`, `Analysis`, `Ingestion` et `Cors`.
- `apps/web/package.json` et son lockfile sont suivis ; utiliser `npm ci`, `npm run check` et `npm run build` depuis ce dossier. Le Dockerfile consomme les mêmes manifests.
- La rétention s'exécute via le worker et sa maintenance horaire ; les migrations versionnées sont exécutées avant le serveur web par le point d'entrée de l'image.

## Boundaries

### Always

- Écrire les tests .NET avec xUnit v3 et les tests TypeScript avec Vitest dans des fichiers `*.test.ts`/`*.test.tsx` ; ne pas créer d'exécutables d'assertions ni de fichiers `*.check.mjs`.
- Privilégier une solution standard et largement documentée de l'écosystème (framework, bibliothèque, outil reconnu) plutôt qu'un mécanisme réinventé « maison ». Avant de concevoir un mécanisme custom (parsing manuel, harness de test artisanal, wrapper autour d'une primitive standard, etc.), vérifier qu'aucune solution établie ne couvre le besoin à un coût raisonnable. Si le repo a déjà une convention custom existante pour ce besoin, la suivre à l'identique plutôt que d'en inventer une nouvelle variante ; en cas de doute entre solution standard et solution maison, demander confirmation avant d'implémenter.
- Lire et respecter [DESIGN.md](DESIGN.md) avant toute modification UI/frontend (écran, composant, CSS, interaction ou état), y compris lors d'une délégation à un autre agent IA.
- Lire [docs/architecture.md](docs/architecture.md) et [docs/integration.md](docs/integration.md) avant de toucher une route d'analyse, d'auth ou de gate.
- Modifier `apps/core` en sachant que l'extension VS Code, l'extension Visual Studio **et** le serveur MCP en dépendent : relancer les checks des paquets concernés. Ne pas dupliquer dans `apps/vscode`, `apps/visualstudio` ou `apps/mcp` une transformation qui appartient au cœur — deux implémentations donneraient deux verdicts pour le même dépôt.
- Ne jamais rendre une mesure absente comme zéro, ni un producteur non exécuté comme « aucune issue », dans aucune surface (hub, VS Code, Visual Studio, MCP).
- **Monter la version avant de pousser une PR qui touche ce qui est livré.** Un changement sous `apps/vscode/`, `apps/core/` ou `apps/mcp/` monte `version` dans `apps/vscode/package.json` ; un changement sous `apps/core/` ou `apps/mcp/` monte aussi `apps/mcp/package.json` ; un changement sous `apps/visualstudio/`, `apps/core/` ou `apps/mcp/` monte `apps/visualstudio/package.json` (et le `<Version>` du csproj, gardé aligné par `src/version.check.mjs`). Le cœur est embarqué dans les trois paquets livrés. Les pipelines lisent la version depuis ces manifestes et refusent de republier une version existante ; sans montée, le VSIX ou le tarball est indiscernable du précédent et un poste déjà équipé ne voit jamais la mise à jour. `npm version patch --no-git-tag-version` dans le dossier concerné. La garde de PR des pipelines `build-vscode-extension.yml`, `build-visualstudio-extension.yml` et `build-mcp-server.yml` rejette la PR autrement.
- Fournir une base PostgreSQL de dev isolée avant de démarrer l'API (le démarrage crée/patch des tables et peut déclencher une purge).
- Mettre à jour `docs/*.md` avec tout changement de comportement observable.
- **Produire une présentation pour chaque feature.** Une PR `feat` (nouvelle capacité ou changement de comportement observable ; pas un correctif, une PR de documentation seule ou une tâche technique) ajoute dans la même PR un support daté `docs/presentations/<feature>.html` et sa ligne dans [docs/presentations/README.md](docs/presentations/README.md), qui en fixe le format et le contenu. La présentation ne fait jamais autorité : elle renvoie au document de domaine, n'annonce que des vérifications réellement exécutées et ne contient aucun secret.

### Ask First

- Changer le contrat de `POST /api/v1/analyses` (consommé par le runner partagé `Up.France.ODI.AzurePipelines`).

### Never

- Copier un token, mot de passe ou log contenant des secrets dans `docs/` ou ce fichier.
- Utiliser une connexion PostgreSQL de production pour un test local.
- Présenter `docker-compose.yml` comme la référence de déploiement (c'est `.azuredevops/deploy-apps.yml`, voir [architecture.md](docs/architecture.md)).

## Code UI/Frontend

- [DESIGN.md](DESIGN.md) est la référence obligatoire du design system : thèmes clair et sombre techniques, tokens partagés, composants natifs, accessibilité et présentation honnête des résultats.
- Réutiliser le système existant pour tout nouvel écran. Les recommandations d'un skill générique ne justifient pas une autre identité visuelle ou une nouvelle bibliothèque UI.
- Lors d'une évolution visuelle explicitement demandée, mettre à jour `DESIGN.md`, les styles concernés et la documentation `docs/` ensemble. Voir [docs/interface.md](docs/interface.md) pour les comportements et validations frontend.
