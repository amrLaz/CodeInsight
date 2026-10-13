---
title: Documentation Up.France.CodeInsights
description: Parcours de lecture pour utiliser, intégrer et faire évoluer le hub de qualité et de sécurité.
---

# Documentation Up.France.CodeInsights

Le code versionné fait autorité sur les capacités disponibles. Chaque document décrit un domaine dans son état courant ; les recommandations et capacités futures sont regroupées dans la [roadmap](roadmap.md). La présence d'un écran ou d'une configuration ne prouve pas une intégration opérationnelle.

## Utiliser le hub

| Document | Public et contenu |
| --- | --- |
| [Fonctionnement et écarts](fonctionnement.md) | Développeurs, responsables qualité et sécurité : capacités, limites et comparaison avec Sonar |
| [Interface](interface.md) | Utilisateurs et contributeurs frontend : parcours, lecture des résultats, administration et vérification |
| [Traitement des issues](collaboration.md) | Équipes : identité durable des findings, assignation, décisions et exceptions |
| [Revue sécurité](security-review.md) | Équipes sécurité : politique de revue des hotspots, décisions figées et taux de revue |
| [Tendances de portefeuille](portfolio.md) | Responsables et équipes qualité : évolution quotidienne des projets autorisés, ce que chaque courbe prouve |
| [Ratings MQR](mqr-ratings.md) | Équipes qualité et plateforme : calcul A–E, complétude et gate New Code des profils MQR |
| [Extension VS Code](vscode-extension.md) | Développeurs et plateforme : rapport de branche, analyse locale et distribution interne |
| [Extension Visual Studio](visualstudio-extension.md) | Développeurs et plateforme : même cœur que VS Code, adaptateur Visual Studio 2022/2026 |
| [Serveur MCP](mcp-server.md) | Développeurs et plateforme : accès du hub et de l'analyse locale depuis un agent IA |

## Intégrer un projet

| Document | Public et contenu |
| --- | --- |
| [Intégration](integration.md) | Équipes projet : contrats de publication, ingestion multi-rapports, New Code et retours PR |
| [Pipelines et scanners](pipelines-scanners-codeinsights.md) | Équipes projet et plateforme : [langages couverts](pipelines-scanners-codeinsights.md#langages-et-stacks-couverts), pipelines .NET 10/React/Flutter/PHP/Terraform, tâches et artefacts |
| [Profils et périmètres](profiles.md) | Équipes qualité et plateforme : profils versionnés, catalogue, configurations natives, activation des règles, périmètres |
| [Politique de licences](licenses.md) | Équipes plateforme et juridique : classement des composants SBOM, classification initiale, inventaire des licences non classées |
| [Nouveau code](new-code.md) | Équipes projet : conditions `new_code`, comparaison avec la baseline et preuves de calcul |
| [Preuves des rapports](report-evidence.md) | Équipes plateforme : ce que l'import conserve de SARIF, couverture, duplication, taille et complexité |

## Exploiter et faire évoluer

| Document | Public et contenu |
| --- | --- |
| [Architecture](architecture.md) | Équipe plateforme : composants, flux, données et hébergement Azure |
| [Autorisations](security-access.md) | Équipe plateforme : identités, rôles, grants, jetons et scopes |
| [Exploitation](exploitation.md) | Exploitants et contributeurs : configuration, développement local, tests et diagnostic |
| [Performances](performances.md) | Équipe plateforme : limites, budgets, protocole et outil de charge |
| [Roadmap produit](roadmap.md) | Responsables produit et équipes : acceptations en attente, extensions à décider, écarts avec la vision cible |
| [Présentations](presentations/README.md) | Équipes et relecteurs : supports visuels datés d'un lot livré, par exemple les SLA par sévérité |

La cible concerne quelques centaines de projets Azure DevOps. Elle repose sur les moteurs d'analyse existants ; elle ne revendique pas la parité des règles ou de la précision des analyseurs SonarQube.
