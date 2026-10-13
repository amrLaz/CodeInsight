---
title: Extension VS Code CodeInsights
description: Architecture cible, périmètre fonctionnel et plan de développement de l'extension VS Code donnant accès au rapport de branche et à une analyse locale des changements en cours.
---

# Extension VS Code CodeInsights

## Objet et statut

Cette page décrit l'architecture cible de l'extension VS Code `upcoop.codeinsights`
hébergée dans [apps/vscode](../apps/vscode/). **Les lots L0 à L9 sont livrés** : le squelette, ses dépendances côté hub — jeton d'accès personnel,
résolution dépôt → projet, lien de vue corrigé —, la lecture du dernier rapport de la
branche courante, sa liste d'issues filtrable, leur publication dans le panneau Problèmes
avec position recalée sur le working tree, leur traitement depuis l'éditeur avec le contexte
pour l'assistant, une console dans le panneau du bas, l'analyse locale des changements en
cours avec une estimation honnête du gate, les signaux V3 (couverture, duplication,
dépendances, règles) et le packaging interne. Les jalons M1 à M4 sont atteints côté
code ; la comparaison écran par écran avec le hub sur les trois pilotes reste à faire.
Cette
page complète le [contrat d'intégration](integration.md), qui reste la référence des
routes réellement disponibles, et la [matrice des autorisations](security-access.md).

Objectifs, dans l'ordre :

1. consulter dans l'IDE le dernier rapport de la branche courante sans ouvrir le hub ;
2. estimer l'effet des changements non commités avant le push et la CI ;
3. traiter une issue là où elle se corrige, y compris avec l'assistant du poste.

[`docs/fonctionnement.md`](fonctionnement.md) rappelle que l'intégration IDE n'est
pas acquise par l'agrégation de rapports : l'extension consomme des rapports
produits par la CI et, pour le mode local, un sous-ensemble volontairement réduit
de scanners. Elle ne reproduit pas le moteur du hub.

## Points d'appui existants

| Besoin                                             | Ressource actuelle                                                                                                                                                                                                                                                                                                              |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Client typé                                        | `GET /openapi.json` (`app.MapOpenApi`), JSON `snake_case` ([ApiJson.cs](../apps/api/ApiJson.cs))                                                                                                                                                                                                                                |
| Dernière analyse d'une branche                     | `GET /api/v1/analyses?project_key=&branch=&kind=branch` puis, à défaut, `kind=pull_request` (curseur, `X-Next-Cursor`) puis `GET /api/v1/analyses/{id}`. L'API accepte le nom court de Git comme le ref complet stocké par la CI ([forme de la branche](integration.md#forme-de-la-branche)) ; l'extension essaie les deux, pour rester lisible face à un hub non migré. |
| Verdict et conditions                              | `gate_snapshot`, `completeness`, `technical_state` calculés par [GateEvaluation](../apps/api/Services/GateEvaluation.cs)                                                                                                                                                                                                        |
| Issues filtrables                                  | `GET /api/v1/analyses/{id}/findings` (`severity`, `category`, `rule`, `path`, `state`, `mine`, `team_id`) et `/components`                                                                                                                                                                                                      |
| Branches connues                                   | `GET /api/v1/insights/branches?project_key=`                                                                                                                                                                                                                                                                                    |
| Catalogue de règles                                | `GET /api/v1/rules?project_key=`                                                                                                                                                                                                                                                                                                |
| Couverture et duplication par fichier et par ligne | `GET /api/v1/insights/analyses/{id}/code-quality`, `/coverage/files`, `/coverage/lines`, `/duplications` ([CodeQualityInsightEndpoints](../apps/api/Endpoints/CodeQualityInsightEndpoints.cs))                                                                                                                                  |
| Traitement                                         | `/api/v1/collaboration/findings/{id}` (état, commentaires, décisions), `POST /api/v1/projects/{key}/exceptions`                                                                                                                                                                                                                 |
| Contexte pour l'IA                                 | `GET /api/v1/issues/{occurrence_id}/prompt` (20 lignes avant/après, session requise)                                                                                                                                                                                                                                            |
| Liens profonds vers le hub                         | état d'URL du SPA : `?tab=issues&project=&branch=&analysis_id=&issue_file=&severity=` ([navigation-state.ts](../apps/web/src/workspace/navigation-state.ts))                                                                                                                                                                    |
| Portée « nouveau code »                            | `git_diff.files[].added_lines` du manifeste, déjà utilisé côté serveur ([IngestionProcessor](../apps/api/Services/IngestionProcessor.cs))                                                                                                                                                                                       |
| Chemins                                            | [SourcePaths](../apps/api/Services/SourcePaths.cs) : les findings sont stockés relatifs au dépôt, donc directement mappables sur le workspace                                                                                                                                                                                   |

## Dépendances côté hub

Les trois premières sont livrées par le lot L1 ; les deux dernières restent ouvertes.

1. **Identité poste de travail — livrée.** Une session est un JWT HS256 de 480 minutes
   et les tokens CI sont créés par un administrateur : aucun des deux ne convient à un
   poste développeur. `POST /api/v1/auth/me/tokens` crée désormais un jeton personnel
   porté par [AuthService](../apps/api/Auth/AuthService.cs), avec l'identité et les
   droits exacts de son propriétaire, une expiration obligatoire, une empreinte
   SHA-256, une révocation idempotente et une entrée d'audit. Il est en lecture seule
   sauf scope `collaboration:write`, n'administre jamais et ne peut pas créer d'autre
   jeton. Les règles complètes sont dans [security-access.md](security-access.md).
2. **Résolution dépôt → projet — livrée.** `GET /api/v1/projects?repo_url=` filtre la
   liste déjà restreinte aux projets autorisés en comparant les identités canoniques
   des URL de clone (HTTPS, SSH, `.git`, identifiant de clone Azure Repos) calculées
   par [RepositoryUrls](../apps/api/Services/RepositoryUrls.cs). Une URL illisible
   renvoie 422, une URL inconnue une liste vide.
3. **Lien de vue — livré.** `GET /api/v1/analyses/{id}/view` redirige vers
   `Ingestion__PlatformUrl` et non plus vers une origine de développement en dur ;
   sans configuration, la route répond 503 au lieu d'un lien faux.

Le jeton se crée depuis le hub : l'avatar de l'appbar ouvre le dialogue
« Personal access tokens », décrit dans [interface.md](interface.md). L'extension ne
crée jamais de jeton elle-même ; elle le reçoit collé dans `SecretStorage`. 4. **Analyse d'aperçu (non retenue en première version).** `POST /api/v1/analyses/preview` :
normalisation, déduplication et évaluation du gate **sans** persistance,
sans historique, sans décoration PR ni notification. Sans cette route, la
simulation reste locale et déclarée comme estimation. 5. **Gestionnaire d'URI inverse.** Pour la ligne « Ouvrir dans l'IDE » du
[TODO](../TODO.md), le SPA doit émettre `vscode://upcoop.codeinsights/open?project=&branch=&sha=&path=&line=`
et l'extension vérifier la correspondance exacte branche/SHA avant de sauter.

## Périmètre fonctionnel

### MVP — lire le dernier rapport de ma branche

- Détection automatique du dépôt, de la branche et du SHA (API Git intégrée de VS Code),
  résolution du `project_key`, mémorisation par dossier de workspace.
- Vue latérale **CodeInsights** : verdict du gate, état technique, complétude,
  métriques Overall et New Code, âge et SHA de l'analyse, écart entre le SHA analysé
  et le HEAD local (`n commits d'avance`).
- **Code couleur du rapport de branche** : l'icône porte la couleur du résultat publié —
  vert pour un `pass` et une note A, bleu pour B, ambre pour C, orange pour D, rouge pour
  E et pour un `fail`, gris atténué pour `unknown`, `absent`, `failed` et `not_measured`.
  Un compteur brut (vulnérabilités, points sensibles, couverture) reste sans icône ni
  couleur : il n'a pas de seuil propre, seule la condition du gate en porte un. Les
  criticités High prennent le rouge profond `codeinsights.severityHigh`, contribué par le
  manifeste et surchargeable dans `workbench.colorCustomizations`. Le libellé reste écrit
  à côté de l'icône, la couleur ne porte jamais seule l'information.
- Arbre des issues groupées par fichier, sévérité ou catégorie ; filtres
  `nouveau code`, `mes issues`, `état`.
- Élément de barre d'état : `CodeInsights: pass | fail | unknown` + nombre d'issues bloquantes.
- Ouverture du fichier et de la ligne exacte ; commande « Ouvrir dans CodeInsights »
  (lien profond avec les filtres courants).
- Rafraîchissement manuel et périodique ; notification quand une nouvelle analyse
  de la branche est terminée.
- **Builds de branche et de pull request** : le rapport est celui du dernier build de
  la branche (`kind=branch`). Une branche qui n'a été analysée qu'au travers de pull
  requests affiche le dernier build de PR issu d'elle, nommé `PR #N · source → cible`
  dans l'en-tête, la notification et un nœud **Pull request** de l'arbre : c'est le
  verdict de la PR, pas celui de la branche. « Ouvrir dans CodeInsights » sélectionne
  alors cette PR dans le hub (`branch=pr:<id>`). La logique est dans le cœur partagé
  (`latestAnalysis`, `analysisScope`, `hubBranchKey`), commune à Visual Studio et au
  serveur MCP ([analyses de branche et de pull request](integration.md#analyses-de-branche-et-de-pull-request)).

### V1 — les issues dans l'éditeur

- **Diagnostics** natifs dans le panneau Problèmes, avec source `CodeInsights`,
  code = identifiant de règle, sévérité mappée.
- **Report de dérive** : les positions sont recalculées depuis le SHA analysé jusqu'au
  working tree en rejouant `git diff <sha> -- <fichier>` **ligne par ligne**, corps des
  hunks compris. Une ligne inchangée est replacée exactement ; une ligne réécrite ou
  supprimée est marquée « position approximative » plutôt que placée au hasard ; un
  commit analysé absent du clone local donne « position non vérifiée ». Lire seulement
  les en-têtes de hunks serait sûr mais rendrait approximatives les trois lignes de
  contexte que `git diff` ajoute autour de chaque changement.
- **Survol** : message, règle, catégorie, effort de remédiation, lien vers la
  définition externe de la règle.
- **CodeLens** en tête de fichier : `2 issues · 1 bloquante · nouveau code`.
- **Actions rapides** sur une issue : assigner, commenter, marquer faux positif ou
  risque accepté, demander une exception — via les routes de collaboration, avec
  respect strict des droits renvoyés par l'API.
- **Copier le prompt / Corriger avec l'assistant** : `GET /api/v1/issues/{id}/prompt`
  alimente un participant de chat `@codeinsights` (Language Model API) ou le
  presse-papier si aucun assistant n'est présent. Ce prompt n'existe que si le producteur
  a transmis la source analysée ; sinon l'extension en construit un depuis le fichier
  local, à la position recalée, et l'annonce comme tel plutôt que de le présenter comme
  la révision analysée.

### V1b — la console

- **Emplacement** : le conteneur CodeInsights vit dans le **panneau du bas**, à côté de
  Problèmes et Terminal, et réunit le rapport de branche, la liste des issues et le détail.
- **Localisations secondaires et flux de code** : `evidence.locations` est rendu tel quel
  — suffixe `[+3 locations]`, étapes de flux ordonnées et navigables, localisations liées.
  Elles deviennent aussi des `relatedInformation` natives, donc listées et navigables
  depuis le panneau Problèmes.
- **Plages exactes** : lorsque le producteur fournit des colonnes et que la position est
  fiable, le soulignement couvre l'expression au lieu de la ligne entière. Sur une
  position approximative, la colonne n'est pas utilisée : elle désignerait un autre code.
- **Panneau de détail** : identité, criticité, catégorie, règle, commit analysé,
  localisations et lien vers la définition du producteur. Il **n'affiche aucune
  description de règle** : le catalogue CodeInsights n'en stocke pas et
  [DESIGN.md](../DESIGN.md) interdit d'en inventer une localement. Cette absence est
  écrite dans le panneau plutôt que passée sous silence.

### V2 — analyse locale des changements en cours

- Commande **Analyser mes changements** et exécution à la sauvegarde (désactivable),
  limitée aux fichiers modifiés du diff `merge-base(branche cible)..working tree`.
- Producteurs locaux volontairement restreints et déjà utilisés par la CI :
  `semgrep` (règles déclarées dans `codeinsights.liveAnalysis.semgrepConfig`, par défaut
  [`.codeinsights/semgrep-mqr-v1.json`](../.codeinsights/semgrep-mqr-v1.json)) et
  `gitleaks`, plus les diagnostics natifs déjà présents dans l'IDE. Le binaire du PATH
  est utilisé s'il existe ; sinon, et seulement si `liveAnalysis.useDocker` l'autorise,
  l'image figée est lancée sans réseau et avec le dépôt monté en lecture seule. Rien
  n'est installé ni téléchargé à l'insu du développeur : un producteur absent est
  **déclaré absent**, jamais transformé en zéro finding.
- **Simulation de gate** : issues locales projetées sur les lignes ajoutées, agrégées
  avec la dernière analyse de la branche comme référence, puis confrontées aux
  conditions du profil affecté. Le résultat est présenté comme une **estimation**
  et l'écart de méthode est explicite.
- Exceptions et règles désactivées du projet appliquées localement, pour éviter le
  bruit que la CI ne produirait pas.
- Rapport « avant push » : issues introduites, issues corrigées, effet estimé sur le gate.

### V3 — confort et signaux complémentaires

- **Couverture** : décorations de gouttière depuis `/coverage/lines` et `/coverage/files`
  de la dernière analyse, complétées si présent par un LCOV/Cobertura local produit par
  les tests du poste, plus récent que l'analyse.
- **Duplication et complexité** : blocs dupliqués de `/duplications` signalés dans le
  fichier ouvert. `/code-quality` reste une synthèse d'analyse (densité, blocs) : cloc
  et Lizard ne sont pas peints par fichier, le hub n'expose pas ces indicateurs à la
  ligne.
- **Dépendances** : annotation de `package.json`, `*.csproj`, `pubspec.yaml` avec les
  vulnérabilités de composants du dernier CycloneDX.
- **Explorateur de règles** en lecture, recherche et filtre, ouverture depuis une issue.
- **Vérification pré-push** optionnelle (`git hook` proposé, jamais installé d'office).
- **Mode hors ligne** : dernier rapport mis en cache par branche, affiché daté et
  marqué comme périmé.
- Support multi-root et monorepo par `.codeinsights/vscode.json` (`projectKey` pour le
  dépôt, `projects` pour un dossier ou une URL de clone). Fichier versionné, lu aussi par
  le serveur MCP : l'agent et l'IDE nomment alors le même projet pour un même dossier.
- Le même fichier porte `targetBranch` et `semgrepConfig`, qui **priment** sur les réglages
  du poste — à l'inverse de `projectKey`, où le réglage du poste prime. La clé dit quel
  projet on regarde, ce que le poste peut légitimement forcer ; ces deux-là disent avec quoi
  l'analyser, et ce sont ceux de la CI. Un poste qui en choisit d'autres estime un gate que
  la pipeline dément. Validation dans `context/project-file.ts` : une valeur mal typée est
  ignorée, un tableau vide est une déclaration.
- Les réglages déclarés « resource » — `projectKey`, `liveAnalysis.targetBranch`,
  `liveAnalysis.semgrepConfig`, `coverage.localLcov` — sont résolus sur le dossier ouvert,
  y compris pour l'environnement passé au serveur MCP et à Claude Code. Lus sans dossier,
  ils auraient rendu la valeur du poste et fait analyser un dépôt avec les règles d'un autre.

## Architecture de l'extension

Depuis le lot A du [serveur MCP](mcp-server.md), la couche métier vit dans
`apps/core`, partagée avec lui, avec [Visual Studio](visualstudio-extension.md)
et avec le serveur MCP ; `apps/vscode` n'est plus que l'adaptateur VS Code.

```text
apps/core/
  src/api/              -> client HTTP + lectures typées du hub
  src/context/          -> résolution project_key, chemins, diff analysé, ports de l'hôte
  src/report/           -> rapport de branche, cache, couverture, duplication, SBOM, règles
  src/local/            -> diff, producteurs, rapport local, estimation du gate, crochet Git
  src/hub/              -> liens profonds vers le SPA
  src/*.test.ts         -> tests Vitest exécutés par npm run check

apps/vscode/
  package.json          -> manifeste, contributions, configuration, commandes, walkthrough
  src/extension.ts      -> activation, enregistrement des contributions
  src/auth/             -> SecretStorage, saisie du PAT, expiration, statut de session
  src/context/          -> dépôt/branche/SHA via l'extension Git, multi-root, ports de l'hôte
  src/views/            -> arbres, barre d'état, diagnostics, gouttières, webview de détail
  src/chat/             -> participant @codeinsights (Language Model API)
  src/mcp/              -> déclaration du serveur MCP embarqué (dist/mcp-server.mjs)
  src/*.test.ts         -> contrats Vitest avec le cœur et le serveur embarqué
```

Le VSIX embarque aussi le [serveur MCP](mcp-server.md) construit depuis `apps/mcp` :
`npm run package` produit `dist/extension.js` et `dist/mcp-server.mjs`, et l'extension
déclare le second aux agents de l'éditeur. `npm ci` dans `apps/mcp` est donc un
prérequis du build.

Contraintes structurantes :

- **TypeScript + esbuild**, dépendances minimales, alignées sur la sobriété de
  `apps/web` (aucune bibliothèque UI additionnelle).
- **Types dérivés d'`openapi.json`** à la génération, jamais recopiés à la main :
  le contrat `snake_case` change côté API sans prévenir l'IDE.
- **Aucune UI custom là où VS Code en fournit une** : arbres, diagnostics, CodeLens,
  actions rapides. La webview est réservée à la synthèse du gate et suit
  [DESIGN.md](../DESIGN.md) en mappant les tokens sur les variables de thème VS Code
  (`--vscode-*`), sans imposer les couleurs du hub.
- **Honnêteté des mesures** : `absent`, `failed` et `not_measured` ne sont jamais
  affichés comme zéro, y compris dans la simulation locale. C'est la règle du hub,
  elle s'applique identiquement dans l'IDE.
- **Secrets** : PAT dans `SecretStorage` uniquement, jamais dans les settings, les
  logs ou l'état de workspace. Aucune télémétrie par défaut.
- **Réseau** : lecture seule vers l'API en dehors des actions de collaboration
  explicites ; aucun envoi de code source hors du poste tant que le mode aperçu
  serveur n'est pas retenu.

## Plan de développement

Une personne à temps partiel, lots livrables indépendamment. Les durées sont des
jours-homme de développement, hors revue et déploiement.

| Lot        | Contenu                                                                                                                                                                                                 | Dépendances | Charge |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------- | -----: |
| **L0** ✅  | Cadrage, squelette `apps/vscode`, pipeline `build-vscode-extension.yml`, script de génération du client OpenAPI                                                                                         | —           |    2 j |
| **L1** ✅  | API : jeton personnel, résolution `repo_url` → projet, correctif `/{id}/view`                                                                                                                           | L0          |    3 j |
| **L2** ✅  | Socle extension : authentification, contexte Git, résolution du projet, cache hors ligne, barre d'état, vue du rapport de branche ; dialogue de compte du SPA pour créer et révoquer un jeton personnel | L1          |    5 j |
| **L3** ✅  | Arbre des issues groupées par fichier, filtres serveur, ouverture fichier/ligne, liens profonds hub, définition de règle, notification de nouvelle analyse                                              | L2          |    4 j |
| **L4** ✅  | Diagnostics, report de dérive par rejeu du diff, survol, CodeLens                                                                                                                                       | L3          |    5 j |
| **L5** ✅  | Traitement : assignation, états, commentaires, exceptions ; prompt du hub ou local et participant de chat optionnel                                                                                     | L3          |    4 j |
| **L5b** ✅ | Console dans le panneau du bas, localisations secondaires et flux de code, plages exactes, panneau de détail                                                                                            | L4          |    3 j |
| **L6** ✅  | Analyse locale : diff des lignes ajoutées, `semgrep`/`gitleaks` en binaire ou image figée, exceptions et règles désactivées appliquées                                                                  | L4          |    6 j |
| **L7** ✅  | Simulation de gate et présentation honnête de l'estimation                                                                                                                                              | L6          |    4 j |
| **L8** ✅  | V3 : couverture, duplication, dépendances, explorateur de règles, hors ligne, multi-root                                                                                                                | L4          |    5 j |
| **L9** ✅  | Packaging, distribution interne, walkthrough d'installation, documentation `docs/`                                                                                                                      | L2+         |    3 j |

Jalons : **M1 = L0→L3** (lecture du rapport de branche dans l'IDE, utilisable par
les pilotes) ; **M2 = L4→L5** (traitement des issues sans quitter l'éditeur) ;
**M3 = L6→L7** (analyse des changements en cours) ; **M4 = L8→L9** (généralisation).

Critères d'acceptation par jalon :

- **M1** : sur les trois pilotes et sur ce dépôt, le verdict et le nombre d'issues
  affichés dans l'IDE sont identiques à ceux du hub pour la même analyse ; une branche
  sans analyse affiche « aucune analyse », jamais un gate vert.
- **M1 — vérifié le 2026-09-10 sur une pile jetable, pas encore sur les pilotes.** Une
  analyse publiée avec quatre findings est relue par l'extension : verdict, profil,
  conditions du gate et mesures correspondent à la réponse de l'API, les zéros mesurés
  restent des zéros et les mesures non produites restent « Non mesuré ». Les issues sont
  groupées par fichier, pire criticité d'abord, y compris un fichier absent du dépôt local.
  La comparaison écran par écran avec le hub sur les trois pilotes reste à faire.
- **Analyse locale — vérifiée le 2026-09-10 avec `gitleaks` v8.30.0 réel.** Sur un dépôt
  jetable, un secret ajouté par le changement en cours est signalé (`src/app.ts:5`) et le
  même type de secret déjà présent sur une ligne non touchée du même fichier est classé
  « ailleurs », pas « introduit ». La valeur du secret n'apparaît nulle part dans le
  rapport. `semgrep` étant absent, la sortie l'annonce et rappelle que l'absence d'issue
  ne vaut pas absence de problème.
- **Recalage — vérifié le 2026-09-10.** Le scénario `drift-git.test.ts` construit un dépôt Git
  réel, y applique une insertion, une réécriture et une suppression, puis vérifie sur la
  sortie réelle de `git diff` que **toute position présentée comme fiable pointe sur le
  contenu analysé** : 34 ancres fiables sur 40 lignes, aucune erronée, et les six lignes
  réécrites ou supprimées sont bien signalées comme incertaines.
- **Console — vérifiée le 2026-09-10 sur une ingestion SARIF réelle.** Un rapport
  contenant une localisation primaire avec colonnes, une localisation liée et un flux de
  deux étapes est publié par le parcours d'ingestion complet, puis relu : l'extension
  produit `[+3 locations]`, la plage `Col 116→166`, les étapes numérotées dans l'ordre
  affiché et le panneau de détail échappé, avec la mention d'absence de description.
- **M2 — vérifié le 2026-09-10 contre l'API réelle, hors rendu VS Code.** Une assignation
  et un changement d'état envoyés par l'extension sont acceptés (version 1 → 2), le
  commentaire et la décision apparaissent dans le journal `/decisions`, et rejouer la
  version précédente renvoie 409 au lieu d'écraser la décision d'un collègue. Un jeton en
  lecture seule reçoit 403 « Personal access token scope does not allow this action » et
  l'extension renvoie vers la création d'un jeton avec triage. Un utilisateur non
  administrateur ne se voit pas proposer les états d'exception, et l'API refuse de toute
  façon avec « Exceptions require administrator review ».
- **M3** : sur un changement introduisant volontairement un secret et une règle
  Semgrep du profil, l'extension les signale avant le push ; la CI confirme ensuite
  les mêmes findings. Tout écart entre estimation locale et gate CI est mesuré et documenté.
- **M3 — estimation livrée le 2026-09-10, pas encore mesurée sur les pilotes.** L'estimation
  projette les issues locales (et les disparitions des mêmes outils) sur les conditions
  de comptage du dernier `gate_snapshot`. Les libellés sont « aggravation probable »,
  « amélioration possible », « aucune aggravation détectée » ou « non conclusive ».
  Aucun « pass » local n'est affiché : la barre d'état conserve le verdict CI. Une
  condition de rating, couverture ou score n'est pas recalculée. Un producteur local
  absent rend la condition associée « non conclusive » plutôt que verte. La mesure
  d'écart avec la CI sur les pilotes reste à faire.
- **M4** : installation depuis zéro sur un poste vierge en moins de cinq minutes,
  sans configuration manuelle du `project_key`.
- **M4 — packaging livré le 2026-09-10.** Le walkthrough d'installation, le VSIX
  produit par `.azuredevops/build-vscode-extension.yml` et la publication optionnelle
  vers Azure Artifacts (`CODEINSIGHTS_VSIX_FEED`) sont en place. L'installation sur
  un poste vierge n'a pas encore été chronométrée.

## Décisions arrêtées

Arbitrées le 2026-09-09, elles conditionnent les lots et ne sont pas rouvertes sans
mise à jour de cette page.

1. **Identité : PAT utilisateur.** Une nouvelle route `POST /api/v1/auth/me/tokens`
   permet à l'utilisateur de créer lui-même un jeton de lecture, avec expiration
   obligatoire, empreinte SHA-256 stockée, révocation individuelle et entrée d'audit.
   Le jeton porte les droits exacts de son propriétaire, jamais plus. Les tokens CI
   restent réservés aux pipelines.
2. **Analyse locale : producteurs locaux et estimation assumée.** Le code ne quitte
   pas le poste et aucun historique projet n'est créé. La simulation de gate est
   présentée comme une estimation, avec l'écart de méthode explicite.
   `POST /api/v1/analyses/preview` reste une évolution possible du lot L7, non retenue
   pour la première version.
3. **Distribution : VSIX interne.** Publication dans Azure Artifacts, cohérente avec
   l'extension Azure DevOps `upcoop.upcoop-code-quality`. Pas de publication sur la
   Marketplace publique tant que le paquet référence l'organisation.
4. **Cible IDE : VS Code comme référence, Visual Studio comme second adaptateur.**
   L'usage de l'API de chat propriétaire reste isolé dans `src/chat/` derrière une
   capacité optionnelle. Visual Studio 2022/2026 consomme le même `apps/core` via
   l'hôte Node d'[apps/visualstudio](../apps/visualstudio/) ; voir
   [visualstudio-extension.md](visualstudio-extension.md). Deux implémentations des
   transformations sont refusées.

## Risques

- **Divergence de verdict** entre la simulation locale et le gate CI : perte de
  confiance immédiate. Mitigation : vocabulaire d'estimation, mesure de l'écart sur
  les pilotes, jamais de « pass » affiché localement.
- **Dérive des positions** entre le SHA analysé et le working tree : annotations
  fausses. Mitigation : recalage par diff et dégradation explicite.
- **Coût des scanners locaux** (installation, durée, faux positifs) : mitigation par
  restriction aux fichiers modifiés, aux images figées et à l'exécution sur demande.
- **Couplage au contrat API** : mitigation par génération depuis `openapi.json` et
  échec de build en cas de rupture.
- **Fuite de secret** : le PAT ne transite ni par les settings synchronisés ni par
  les logs ; expiration obligatoire et révocation depuis le hub.

## Validation

- `npm run check` dans `apps/vscode` sur le modèle de `apps/web` : `tsc --noEmit`
  plus des tests Vitest sur le mapping des findings, le report de dérive, la
  construction des liens profonds et la présentation des mesures non mesurées.
- Scénarios d'intégration contre une API locale (`docker compose up -d`) : branche
  sans analyse, analyse `unknown`, analyse incomplète, PAT expiré, projet non autorisé.
- Les tests API existants ([CodeInsights.Tests](../tests/CodeInsights.Tests) et
  [routes.json](../tests/contracts/routes.json)) sont étendus aux nouvelles routes du lot L1.
- La documentation `docs/` et [security-access.md](security-access.md) sont mises à
  jour avec le PAT utilisateur dans le même changement que son implémentation.
