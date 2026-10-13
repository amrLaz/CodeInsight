# TODO

- [ ] Sur les issues, donner une estimation de temps nécessaire pour les résoudre.
- [x] Mise en favori des projets par utilisateur
- [x] Affichage de l'évolution de toutes les métriques dans le dashboard projet et suppression de la vue Evolution. Les métriques de même repère partagent une courbe ; les autres groupes utilisent des onglets.

- [x] Ajouter un splash screen futuriste à l'application pour améliorer l'expérience utilisateur au démarrage.
- [x] Ajouter des animations subtiles pour les transitions entre les vues afin d'améliorer l'expérience utilisateur.
- [x] Simplifier la navigation à Projects, Issues, Rules, Quality Gates et Administration.
- [x] Ouvrir Issues depuis un projet ou une criticité du dashboard avec le filtre correspondant.
- [x] Depuis une issue, ouvrir le code au commit analysé et la définition externe connue de la règle.
- [x] Distinguer visuellement les cartes Issues par criticité sans dépendre uniquement de la couleur.
- [x] Supprimer les routes, services, tables et migrations des vues retirées ; conserver une migration initiale unique tant que le produit n'est pas en production.

- [x] Revoir l'UX/UI de la vue Quality Gate
- [x] corriger la vue Rules
  - [x] supprimer le scroll imbriqué et conserver un seul défilement de page
  - [x] activer et désactiver les règles prises en charge par une nouvelle version de profil
- [x] La navigation et les filtres métier persistent dans l'URL
- [x] Décoration des PR avec les résultats de l'analyse et les échecs du Quality Gate. C'est une fonctionnalité activable depuis la vue d'administration.
- [x] Mode sombre activable par utilisateur et persisté dans le local storage et profiter de cette feature pour trouver la meilleure façon de spliter l'énorme fichier styles.css.
- [x] Downsampling des métriques pour les vues historiques afin d'améliorer les performances.

## Prochaines étapes Sonar-like

- [ ] Faire de New Code le résumé prioritaire d'un projet : gate, issues nouvelles, couverture et duplication depuis la baseline.
- [ ] Ajouter aux filtres Issues des facettes avec compteurs serveur et conserver tous les filtres dans l'URL partageable.
- [ ] Ajouter les actions de triage en lot dans Issues : assignation, état, faux positif et risque accepté selon les droits.
- [ ] Ajouter « Mes issues » comme filtre d'entrée direct, sans recréer une vue Collaboration.
- [ ] Afficher sur chaque projet le Quality Gate courant et les métriques essentielles : bugs, vulnérabilités, code smells, couverture et duplication.
- [ ] Ajouter les événements utiles dans l'historique projet : changement de gate, profil, version analysée et rupture de mesure.
- [ ] Prévoir « Ouvrir dans l'IDE » seulement lorsqu'un protocole IDE fiable et la correspondance exacte branche/SHA sont disponibles.

- [ ] Piloter le nettoyage et la convergence des pipelines avec la [roadmap CodeInsights](#roadmap-restante-codeinsights).
- [ ] Mettre à jour la documentation pour refléter les changements récents dans l'application. Une documentation de l'architecture avec des graphes et des explications sur les flux de données serait idéale.

## Extension Visual Studio

Plan et décisions dans [docs/visualstudio-extension.md](docs/visualstudio-extension.md).

- [x] Hôte Node JSON-RPC sur `apps/core` (même verdict que VS Code et le serveur MCP).
- [x] Adaptateur VisualStudio.Extensibility : options, PAT DPAPI, fenêtre d'outils, liste d'erreurs, commandes.
- [ ] Packaging VSIX sur agent Windows et installation pilote.

## Extension VS Code

Plan et décisions arrêtées dans [docs/vscode-extension.md](docs/vscode-extension.md).

- [x] L0 — Squelette `apps/vscode`, build du VSIX et génération du client depuis `openapi.json`.
- [x] L1 — API : PAT utilisateur `POST /api/v1/auth/me/tokens`, résolution `GET /api/v1/projects?repo_url=`, correction de la redirection `/api/v1/analyses/{id}/view`.
- [x] L2 — Socle : authentification, contexte Git, cache, barre d'état et rapport de branche, plus le dialogue de compte du SPA pour créer et révoquer un jeton personnel.
- [x] L3 — Arbre des issues, filtres, ouverture fichier/ligne et liens profonds (jalon M1).
- [x] L4 — Diagnostics, report de dérive, survol et CodeLens.
- [x] L5 — Collaboration et prompt IA depuis l'éditeur (jalon M2).
- [x] L5b — Console dans le panneau du bas : localisations secondaires, flux de code, plages exactes et panneau de détail.
- [x] L6 — Analyse locale des changements en cours (`semgrep`, `gitleaks`, diff).
- [ ] L7 — Simulation de gate présentée comme estimation (jalon M3).
- [ ] L8 — Couverture, duplication, dépendances, règles, hors ligne et multi-root.
- [ ] L9 — Packaging, distribution Azure Artifacts et documentation (jalon M4).

## Serveur MCP

Plan et décisions arrêtées dans [docs/mcp-server.md](docs/mcp-server.md).

- [x] A — Cœur partagé `apps/core` : `api/`, `report/`, `local/`, `context/`, `hub/` et ses tests Vitest ; port `context/ports.ts` servi par VS Code et par Node.
- [x] B — Serveur stdio `apps/mcp` et six outils de lecture, bornes de sortie, erreurs actionnables, `protocol.test.ts`.
- [x] C — `analyze_pending_changes` : producteurs locaux, exceptions du projet, estimation du gate, résultat partiel annoté.
- [x] E — Distribution : bundle `dist/mcp-server.mjs` embarqué dans le VSIX, fournisseur `codeinsights.mcp`, pipeline `build-mcp-server.yml`, tarball autonome.
- [ ] E bis — Créer le flux npm Azure Artifacts et le scope `@upcoop`, puis renseigner `CODEINSIGHTS_NPM_FEED` sur le pipeline ; sans elle, le tarball est construit et vérifié mais pas publié.
- [ ] F — Pilote : installer le VSIX et vérifier que les outils apparaissent dans Copilot agent mode sans configuration, puis que `get_branch_report` rend sur le hub réel le même verdict et le même nombre d'issues que le hub, sur ce dépôt et sur les trois pilotes.
- [ ] D — Triage en écriture (`triage_finding`), seulement après le pilote et selon ce qu'il montre de l'usage des outils de lecture.

## Roadmap restante CodeInsights

Les profils API sont déployés. Le contrat courant repose sur l'extension `0.5.0`
et les templates AzurePipelines `v3.1.0` : chaque CI utilise directement le
wrapper adapté à sa stack, sans chemin de compatibilité parallèle.

### P0 — Stabiliser les pilotes existants

- [x] Passer à `v1.2.9` les pipelines SAST des branches `feature/add-sast-pipeline` de StoreLocator Web, StoreLocator Data et Commercant Web.
- [x] Conserver temporairement `upcoop-security/1` et `failOnQualityGate: false`.
- [x] Exécuter les trois pipelines et vérifier en PROD : onglet CodeInsights, quatre scanners de sécurité, JSCPD, CLOC, Lizard, absence d'`unknown` technique et liens vers les analyses.
- [x] Consigner les build IDs, durées et métriques obtenues comme baseline de migration.

| Pilote            | Build PROD |      Durée | Blocs dupliqués | Lignes de code | Complexité | Gate informatif | État technique |
| ----------------- | ---------: | ---------: | --------------: | -------------: | ---------: | --------------- | -------------- |
| Commercant Web    |     276500 | 1 min 54 s |             207 |         92 907 |      9 128 | fail            | succeeded      |
| StoreLocator Web  |     276501 | 1 min 35 s |             153 |         45 021 |      2 121 | fail            | succeeded      |
| StoreLocator Data |     276502 | 1 min 26 s |              78 |         13 401 |        672 | pass            | succeeded      |

### P1 — Manuel et skill d'intégration

- [x] Publier le skill par le mécanisme de distribution existant dans `Up.France.Ai.Registry` (`copilot-orchestrator` `1.0.66` et `upmigrator` `1.0.8`) et valider ses trois copies avec `quick_validate.py`.
- [ ] Confirmer le chargement du skill publié dans une nouvelle session Codex après rafraîchissement du registre local.
- [x] Créer le manuel canonique `Up.France.ODI.AzurePipelines/docs/migration-codeinsights.md` : prérequis, matrice par stack, exemples avant/après, profils, secrets, artefacts, validation, performances, rollback et dépannage.
- [x] Référencer ce manuel depuis les index AzurePipelines et CodeInsights sans dupliquer les contrats techniques.
- [x] Ajouter au skill `upcoop-azure-pipelines` un mode d'intégration qui inspecte le pipeline existant, conserve ses builds/tests et produit un patch minimal.
- [x] Ajouter une référence CodeInsights dédiée au skill avec les parcours .NET, React, Python, sécurité seule et accessibilité.
- [x] Supprimer le fallback statique `v1.1.7` : conserver un tag stable seulement s'il contient les templates requis, sinon rechercher le dernier tag approuvé et demander le tag si cette vérification est impossible.
- [x] Faire valider par le skill les profils, producteurs obligatoires, artefacts inter-jobs, états `0`/`not_measured`/`unknown`, mode du gate et absence de secrets.

### P2 — Architecture de publication découplée

- [x] Implémenter `UpCoopSecurityScan@2` en mode scan uniquement, avec `outputDirectory` et un fragment de manifeste par producteur.
- [x] Implémenter `UpCoopPublishReports@1` comme frontière unique avec CodeInsights : validation, ingestion idempotente, envoi, attente du gate et attachment `BuildScanSnapshotV1`.
- [x] Faire accepter à la tâche de publication un `reportsRoot` contenant les fragments de manifeste, afin de supporter un job unique ou des artefacts provenant de jobs parallèles.
- [x] Faire produire un manifeste compatible par `codeinsights-dotnet-reports-tpl.yml`.
- [x] Ajouter uniquement les templates de producteurs/publication nécessaires ; ne pas dupliquer les templates complets de build .NET ou React.
- [x] Conserver Roslyn, les tests et la couverture sur l'agent ; aucun secret NuGet ou npm ne doit entrer dans le conteneur scanner.

### P3 — Profils complets par stack

- [ ] Migrer StoreLocator Web et Commercant Web sur ce profil, puis vérifier toutes les métriques en PROD.
- [ ] Migrer StoreLocator Data sur ce profil et vérifier toutes les métriques en PROD.
- [x] Créer le producteur React consommant les rapports déjà générés par Jest/Vitest et ESLint, sans relancer tests ou build.
- [x] Ajouter `upcoop-react/1` avec ESLint SARIF, LCOV, sécurité, duplication, lignes et complexité.
- [x] Ajouter `upcoop-python/1` avec Ruff SARIF, couverture Cobertura, sécurité, duplication, lignes et complexité.
- [x] Créer le producteur Python réutilisant Ruff, pytest et coverage.py.
- [x] Ajouter `upcoop-flutter/1` avec `dart-analyze` SARIF, LCOV Flutter (lignes et branches), sécurité, duplication, lignes et complexité Dart.
- [ ] Créer le producteur Flutter réutilisant `dart analyze`, `flutter test` et les convertisseurs SARIF, JUnit et cppncss, sans relancer le build applicatif.
- [ ] Migrer un pilote Flutter sur `upcoop-flutter/1` et vérifier toutes les métriques en PROD.
- [x] Ajouter `upcoop-php/1` avec PHPStan SARIF, couverture PHPUnit Cobertura (lignes), sécurité, duplication, lignes et complexité Lizard.
- [x] Passer `upcoop-php/1` en MQR : catalogue PHPStan/Larastan, Semgrep `p/ci` et `p/php`, secrets et Dockerfile ; ratings et duplication calculés.
- [ ] Publier le producteur PHP (`az-php-codeinsights.yml`) dans un tag AzurePipelines vérifié.
- [ ] Migrer le pilote `Up.France.ODI.Services.Beneficiaire.LogiwebServices` sur `upcoop-php/1`, puis CardsServices, UserServices et NexusFunctionsServices.
- [x] Ajouter l'import TRX/JUnit pour les nombres de tests, échecs, ignorés et durées.
- [x] Conserver le scan d'accessibilité comme parcours post-déploiement séparé et intégrer son rapport natif sans conversion SARIF destructive.
- [x] Maintenir `license_violations` à `not_measured` jusqu'à la publication d'une politique de licences versionnée.

Recette des définitions principales sur `feature/add-sast-pipeline` :

| Dépôt             |    PR |                        Build principal | État au 9 septembre 2026                                                                                 |
| ----------------- | ----: | -------------------------------------: | -------------------------------------------------------------------------------------------------------- |
| StoreLocator Web  | 50511 | 276735, puis 276765 sur le merge de PR | publication PROD validée ; build partiel à cause des contrôles TypeScript, Prettier et SBOM préexistants |
| Commercant Web    | 50513 |                           276766 en PR | publication PROD validée après transmission des reporters au script Jest imbriqué                        |
| StoreLocator Data | 50512 |              276736, puis 276758 en PR | bloqué avant publication par le `ruff format --check` préexistant sur quatre fichiers du dépôt           |

Le build `276735` utilise la définition principale `941`, et non le pilote SAST
`985`. Azure DevOps développe les templates avant exécution : les tâches internes
du wrapper React apparaissent donc directement dans les logs. Les équivalences
sont `162`/`984` pour Commercant Web et `845`/`986` pour StoreLocator Data.

| Analyse PROD                                            | Couverture | Branches | Tests | Vulnérabilités | Hotspots | Code smells | Duplication | Complexité |
| ------------------------------------------------------- | ---------: | -------: | ----: | -------------: | -------: | ----------: | ----------: | ---------: |
| StoreLocator Web `60d9b4f6-ff51-47b6-97c9-81206eb69e50` |    52,32 % |  61,99 % |   435 |             30 |        1 |           0 |         211 |      2 969 |
| Commercant Web `80aef2d8-ec30-4645-a70b-3d915a5a58c4`   |    39,20 % |  27,52 % |   925 |             17 |        7 |           0 |         275 |     11 933 |

### P4 — Packaging, publication et généralisation

- [ ] Observer cinq builds réussis par dépôt, dont au moins une PR, avant toute proposition de gate bloquant.
- [ ] Intégrer ensuite CodeInsights dans les pipelines CI principaux et désactiver les trois pipelines SAST temporaires.
- [x] Valider l'extension de développement dans l'organisation de test avant partage de la version stable avec l'organisation PROD.
- [x] Verrouiller toutes les images par digest et vérifier les approbations des environments de publication.
- [x] Comparer la durée aux baselines P0 ; le flux ne relance ni build ni tests et reste sous +20 % sur le temps propre aux analyses après chauffe du cache.
- [x] Documenter le rollback : précédent tag de templates, profil `upcoop-security/1`, réactivation du pipeline temporaire et conservation de l'historique d'analyses.

| Pilote            | Baseline analyse P0 |  Analyse v1.3 à chaud |   Écart |
| ----------------- | ------------------: | --------------------: | ------: |
| Commercant Web    |              85,5 s | 86,4 s (build 276541) |  +1,1 % |
| StoreLocator Web  |              77,9 s | 90,1 s (build 276542) | +15,7 % |
| StoreLocator Data |              68,3 s | 79,4 s (build 276539) | +16,3 % |

Livraisons validées : API/profils build 276528, extension de développement
`0.2.276717` build 276717, extension stable `0.3.1` build 276722, templates
`v2.0.0` et pipeline CodeInsights complet build 276540.

### Validation

- [ ] Compilation Azure Pipelines des exemples .NET, React et Python.
- [x] Tests API des profils, formats, états de producteurs et imports, y compris contre PostgreSQL 16 isolé.
- [x] Tests Node des tâches : chemins, manifestes, idempotence, masquage des secrets, échec technique et zéro mesuré.
- [x] `npm ci`, audit, typage, tests et packaging des deux VSIX.
- [x] Smoke test de l'image scanner inchangée et verrouillée par digest dans les trois pilotes PROD.
- [x] Tests PROD des trois pilotes avec contrôle de l'onglet, des artefacts, des métriques et du gate informatif (builds 276537 à 276539, puis 276541 et 276542 à chaud).

### Hypothèses retenues

- Le wrapper React courant compose le build existant ; aucun build ou test n'est réimplémenté. Data conserve sa CI Python inline et n'intègre que les deux templates de qualité/finalisation.
- Aucun gate bloquant n'est activé automatiquement.
- Le manuel AzurePipelines constitue la source de vérité pour les squads ; CodeInsights contient uniquement les contrats API et un lien vers ce manuel.
