# Up.France.CodeInsights

[![Application](https://dev.azure.com/up-group/c1dd9043-ad8d-467e-9cf6-646b6381ef0f/_apis/build/status/969?branchName=master&label=Application)](https://dev.azure.com/up-group/Up.France.ODI/_build?definitionId=969)
[![Infrastructure](https://dev.azure.com/up-group/c1dd9043-ad8d-467e-9cf6-646b6381ef0f/_apis/build/status/963?branchName=master&label=Infrastructure)](https://dev.azure.com/up-group/Up.France.ODI/_build?definitionId=963)
[![Build Status](https://dev.azure.com/up-group/Up.France.ODI/_apis/build/status%2FCodeInsights%2FPackaging%2FCodeInsights%20VS%20Extension?branchName=master)](https://dev.azure.com/up-group/Up.France.ODI/_build/latest?definitionId=993&branchName=master)

L'application **CodeInsights** consolide les analyses de code de plusieurs projets et expose leurs métriques, quality gates, issues et historiques. Le parcours d'ingestion importe SARIF, couverture, CycloneDX et SPDX via Blob et une file PostgreSQL durable ; il ajoute profils versionnés, comparaison Git et retours PR. La publication directe `POST /api/v1/analyses` reste disponible pour le runner partagé. La validation locale ne remplace pas la campagne complète de capacité ni le pilote Azure DevOps.

Le runtime est ASP.NET Core 10 / Entity Framework Core 10 / Npgsql / PostgreSQL avec une interface React. Le [Dockerfile](Dockerfile) embarque l'API et le frontend dans une image Linux.

## Applications

La solution contient trois applications :

- `apps/api` : backend ASP.NET Core 10 (Minimal APIs), authentification, ingestion, calcul des quality gates et persistance PostgreSQL ;
- `apps/web` : frontend React 19 / TypeScript / Vite pour consulter les projets, analyses, métriques, règles et issues ;
- `apps/vscode` : extension VS Code TypeScript / esbuild pour consulter le rapport de la branche, traiter les issues et analyser localement les changements. Elle est distribuée séparément au format VSIX ; voir sa [documentation](docs/vscode-extension.md) ;
- `apps/visualstudio` : extension Visual Studio 2022/2026, même cœur `apps/core` derrière un hôte Node, distribuée en VSIX ; voir sa [documentation](docs/visualstudio-extension.md).

Le frontend est compilé puis servi par le backend dans l'image applicative unique. L'extension VS Code consomme l'API mais n'est pas embarquée dans cette image.

## Structure du projet

```text
.azuredevops/             -> pipelines de build, déploiement et publication de l'extension
apps/
  api/                    -> backend ASP.NET Core 10
  web/                    -> frontend React / TypeScript / Vite
  vscode/                 -> extension VS Code et packaging VSIX
  visualstudio/           -> extension Visual Studio 2022/2026 (hôte Node + VSIX)
docs/                     -> documentation fonctionnelle, technique et d'exploitation
infra/                    -> infrastructure Azure en Terraform
scripts/                  -> outils de validation et de charge
tests/                    -> tests xUnit et contrats de l'API
CodeInsights.slnx         -> solution .NET de l'API et des tests
Directory.Build.props     -> configuration et versions .NET centralisées
Dockerfile                -> image applicative unique frontend + backend
docker-compose.yml        -> environnement de développement local
```

- [Documentation et audit des capacités](docs/index.md)
- [Intégration des builds](docs/integration.md)
- [Exploitation et développement local](docs/exploitation.md)
- [Infrastructure Terraform Azure](infra/PLATFORM_SAST.md)

La solution [CodeInsights.slnx](CodeInsights.slnx) regroupe l'API et ses tests xUnit v3 ; [Directory.Build.props](Directory.Build.props) centralise .NET 10 et les versions des dépendances. Depuis la racine :

```powershell
dotnet build CodeInsights.slnx -c Release
dotnet test tests/CodeInsights.Tests -c Release
```

Le frontend se valide directement, sans passer par Docker :

```powershell
cd apps/web
npm ci
npm run check
npm run build
```

`npm run check` exécute le typage strict puis les tests Vitest TypeScript. La variante `npm run check:ci` génère `test-results/junit.xml`, `coverage/lcov.info` et `coverage/cobertura-coverage.xml` pour Azure Pipelines.

Le test d'intégration `apps/web/tests/insights-api.test.ts` s'exécute seulement avec `CODEINSIGHTS_TEST_URL` et `CODEINSIGHTS_TEST_TOKEN`, sur une API locale isolée contenant au moins une analyse synthétique. Sans ces variables, Vitest l'ignore.

Les tests API utilisent xUnit v3. Les contrôles PostgreSQL et HTTP nécessitent une base isolée et les variables décrites dans le [guide d'exploitation](docs/exploitation.md). Une exécution sans ces variables ne valide pas le serveur ou la persistance.
