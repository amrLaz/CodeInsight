---
title: Intégration des analyses et rapports
description: Contrat actuel de publication et parcours durable multi-rapports Azure DevOps.
---

# Intégration des analyses et rapports

## Choisir le bon parcours

Les templates partagés sont versionnés dans **Up.France.ODI.AzurePipelines**. Son document `docs/report-integration.md` fait autorité sur leurs paramètres et sorties ; le README de ce dépôt y mène. Le [manuel canonique de migration](https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.ODI.AzurePipelines?path=/docs/migration-codeinsights.md) guide les squads sans recopier les contrats API de cette page. Utiliser les paramètres du template partagé.

Le contrat courant repose sur les wrappers CodeInsights .NET, React, Flutter, PHP et
Terraform, complétés par un parcours sécurité seul. Les producteurs génèrent des
rapports standards sans jeton CodeInsights ; `UpCoopPublishReports@1` les réunit
dans une ingestion unique et reçoit seul la service connection
`CodeInsights-Coop`.

Le profil `upcoop-php/1` est un profil MQR : il attend PHPStan (SARIF), la
couverture PHPUnit (`php-coverage`, Cobertura, lignes uniquement), Lizard, jscpd,
cloc et les quatre producteurs sécurité, et calcule les ratings Security,
Reliability, Maintainability et Security review ainsi que la densité de
duplication à partir de son [catalogue](mqr-ratings.md#catalogue-upcoop-php1). Il
n'exige ni `branch_coverage` ni `condition_coverage` : le Cobertura de PHPUnit ne
porte aucun détail de branche par ligne, ces mesures restent donc non mesurées. Les limites et le parcours sont
décrits dans [Pipelines et scanners](pipelines-scanners-codeinsights.md#pipeline-cible-php).

## Publication directe

La source exacte est [AnalysisRequest](../apps/api/Contracts/AnalysisContracts.cs) et les [routes d'analyse](../apps/api/Endpoints/AnalysisEndpoints.cs) ASP.NET Core 10. La route de création est `POST /api/v1/analyses` ; son corps reste inchangé. La collection `GET /api/v1/analyses` sert désormais des synthèses paginées, avec `limit` (1–100, défaut 50), `Link` et `X-Next-Cursor`. Les détails sont consultés séparément.

| Champ / mécanisme | Contrat actuel                                                                                                                                                                  |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Identité          | `project_key` (au moins 2 caractères), `commit_sha` (au moins 6), `branch` non vide, stockée sous sa forme de ref complète (voir ci-dessous)                                    |
| Origine           | `ci_provider` : `azure-devops`, `github-actions` ou `local`                                                                                                                     |
| Détails           | `affected_files` est un dictionnaire de fichiers/findings, pas un document SARIF                                                                                                |
| Contexte PR       | `pr_id`, `pr_repo_id`, `pr_ado_project`, `pr_target_branch`, `pr_ado_org_url`, `platform_url`                                                                                   |
| Complétude        | `measurements` distingue `measured`, `absent`, `failed` et `not_measured`; `tool_executions` conserve le résultat et la version de chaque outil. Zéro est une valeur mesurée.   |
| Profil            | `profile_id`, `profile_version` et `formula_version` doivent correspondre au profil affecté au projet. Les profils version 1 couvrent .NET, React, IaC et le runner partagé. |
| New Code          | Le manifeste multi-rapports peut déclarer `project_version`, `new_code_reference_branch` et, pour une PR, `pr_target_branch`. Ces champs n'étendent pas le corps strict de `POST /api/v1/analyses`. |
| Authentification  | Le Bearer est un token CI actif, non expiré, autorisé pour le projet et doté de `analysis:write`. `analysis:read` et `integration:write` sont séparés.                          |

La publication exige `project_key`, `commit_sha`, `branch`, `ci_provider`, `profile_id`, `profile_version`, `formula_version`, `measurements` et `tool_executions`. Tout champ inconnu est refusé. Le verdict est calculé dans [GateEvaluation](../apps/api/Services/GateEvaluation.cs). La réponse expose `completeness`, `technical_state` et `gate_snapshot`, avec les sept conditions évaluées. Un échec mesuré produit `fail` ; une donnée requise absente ou un outil indisponible produit `unknown` ; seul un jeu complet peut produire `pass`.

[ApiJson.cs](../apps/api/ApiJson.cs) configure les noms JSON `snake_case`. Les booléens sont des valeurs JSON `true`/`false`. Les erreurs utilisent `application/problem+json` et `detail` : 400 pour un corps JSON invalide, 422 pour une validation métier. Les dates sont ISO 8601 UTC. Le catalogue [routes.json](../tests/contracts/routes.json) et les scénarios [CodeInsights.Tests](../tests/CodeInsights.Tests) définissent les tests HTTP.

Les lectures authentifiées `/api/v1/insights/analyses/{id}/code-quality`, `/coverage/files`, `/coverage/lines`, `/duplications` et `/source?path=...` restituent les preuves natives de la dernière révision terminée de l'analyse autorisée. Les listes acceptent `limit` de 1 à 100, un curseur renvoyé par `Link` et `X-Next-Cursor`, ainsi que leurs filtres documentés dans l'interface. Coverage lit les lignes normalisées ; Duplications lit uniquement le snapshot jscpd borné à 50 Mio et 250 000 entrées. Source accepte uniquement un chemin déjà présent dans l'une de ces preuves, vérifie l'association Azure DevOps du projet, puis lit à la demande le fichier UTF-8 au SHA Git exact avec l'identité managée système de l'App Service. Cette identité doit posséder le droit Azure DevOps `Code (Read)` ; les autres droits restent limités aux capacités explicitement activées. La réponse est limitée par `Ingestion__MaxSourceBytes`, porte `Cache-Control: private, no-store` et n'est ni persistée ni réutilisée entre ouvertures. Un import direct via `POST /api/v1/analyses` reste explicitement sans rapport natif : ce contrat d'écriture n'est pas modifié.

`upcoop-dotnet/2` remplace les scores du gate par les ratings MQR Overall/New
Code, attend un unique producteur `roslyn` et classe les règles par `(outil,
ruleId)`, quelle que soit la version du SDK résolue par le dépôt. `tool_version`
reste une provenance obligatoire ; la compatibilité technique des producteurs
repose sur `(outil, format, format_version)`. Ses calculs, causes
d'indisponibilité et seuils sont
documentés dans [Ratings MQR](mqr-ratings.md). L'affectation du profil v2 doit
être coordonnée avec le nouveau tag immuable du template partagé qui supprime
les producteurs historiques `roslyn-bugs` et `roslyn-code-smells`.

## Forme de la branche

La branche est **stockée sous sa forme de ref complète**, `refs/heads/<nom>`, par
[BranchRefs](../apps/api/Services/BranchRefs.cs). C'est celle que les fournisseurs CI
rapportent — `Build.SourceBranch` côté Azure DevOps — donc une publication de pipeline
est inchangée, au caractère près. Un client qui envoie un nom court (`develop`) voit sa
valeur promue en `refs/heads/develop` à l'écriture, à l'ingestion comme sur
`POST /api/v1/analyses` ; la réponse renvoie alors la forme stockée, pas la forme reçue.

En lecture, les filtres `branch` de `/api/v1/analyses`, `/api/v1/insights/*` et
`/api/v1/collaboration/findings` acceptent **les deux orthographes** : `develop` et
`refs/heads/develop` désignent la même branche, et `prefix` sur
`/api/v1/insights/branches` se compare aussi bien au nom court qu'au ref. `search`
sur le même endpoint retient toute branche dont le ref contient le texte, sans tenir
compte de la casse : `master` trouve `refs/heads/master`. Un client
n'a donc pas à deviner ce que la CI a rapporté — c'est ce qui rendait une analyse
publiée par le pipeline invisible pour l'extension VS Code, qui lit le nom court de Git.

La migration `NormalizeBranchRefs` aligne les lignes antérieures (`analysis_records`,
`durable_findings`, `finding_identity_matches`, `archived_code_branches`, `branches`).
Elle ne touche pas l'identité des findings durables : les décisions de triage restent
attachées aux findings sur lesquels elles ont été prises. En revanche, pour un projet
qui publiait jusqu'ici un nom court, les analyses suivantes calculent une identité
fondée sur le ref complet : la continuité des findings s'y interrompt une fois, à la
première analyse après la migration.

## Analyses de branche et de pull request

Une analyse porteuse d'un `pr_id` est une **analyse de pull request**. Elle est
stockée sous sa branche source, mais elle décrit ce que la PR fusionne : sur Azure
DevOps, le build de PR analyse en général le commit de fusion, pas la tête de la
branche. Elle ne tient donc jamais lieu d'analyse de cette branche
([AnalysisKinds](../apps/api/Services/AnalysisKinds.cs)) :

- **Verdict et historique de branche** : `/api/v1/insights/branches` liste toutes
  les branches analysées, mais `count` et `latest` n'y comptent que les builds de
  branche ; `pull_request_count` compte les builds de PR rangés sous la branche. Une
  branche construite uniquement via des PR garde son entrée, avec `latest: null`.
- **Synthèse par projet** : `/api/v1/insights/summary` calcule `latest` et les
  compteurs `pass`/`fail`/`unknown` sur les builds de branche. Un projet qui n'a
  été analysé qu'au travers de PR reste accessible : son `latest` est alors son
  dernier build de PR, identifiable par son `pr_id`.
- **Liste des PR** : `GET /api/v1/insights/pull-requests?project_key=` renvoie une
  ligne par PR (`pr_id`, `source_branch`, `target_branch`, `count`, `latest`), des
  plus récentes aux plus anciennes selon leur numéro, avec la pagination habituelle
  (`limit`, `cursor`, `X-Next-Cursor`). `pr_id=` restreint la liste à une PR.
- **Filtres de liste** : `/api/v1/analyses` et `/api/v1/insights/analyses`
  acceptent `kind=branch|pull_request|all` et `pr_id=`. Sans `kind`, les deux types
  restent listés, comme avant cette séparation, pour les clients qui ne l'envoient
  pas. Une valeur inconnue est refusée (422).
- **Extensions et serveur MCP** : le cœur partagé lit le dernier build de branche
  (`kind=branch`) et, si la branche n'en a aucun, le dernier build de PR issu d'elle,
  toujours nommé `PR #N · source → cible` ([extension VS Code](vscode-extension.md#mvp--lire-le-dernier-rapport-de-ma-branche)).
  Le hub montre au contraire un état vide pour une telle branche, avec ses PR à
  sélectionner : l'IDE n'a pas de sélecteur, mais aucune surface ne présente le
  verdict d'une PR comme celui de la branche.
- **Baseline New Code** : un build de PR n'est jamais retenu comme baseline, quel
  que soit le mode ([nouveau code](new-code.md#comparaison-avec-la-baseline)).
- **Tendances** : les instantanés de portefeuille ne lisent que les builds de
  branche de la branche principale.

La réponse d'une analyse expose `pr_target_branch`, la branche cible de la PR sous
sa forme de ref. L'ingestion l'effaçait jusqu'ici ; la migration
`BackfillPullRequestTargetBranch` la restaure à partir du dernier manifeste
d'ingestion terminé de chaque analyse de PR. Une analyse publiée directement sans
cible conserve une valeur nulle, affichée comme non enregistrée.

Une analyse de PR relie ses occurrences aux findings durables de sa branche source
sans les modifier ; trier une de ses occurrences crée le finding sur la branche
source ([traitement des issues](collaboration.md#analyses-de-pull-request)). La
rétention ne distingue pas encore les deux types : voir la
[roadmap](roadmap.md#analyses-de-pull-request).

## Parcours multi-rapports

Le template canonique `platform-publish-reports-tpl.yml` publie les rapports déjà
produits sans réinstaller les scanners ni relancer les tests. Le contexte ADO est
déduit du build ; l'association dépôt/projet et le profil versionné doivent être
configurés côté hub. Le mode `full` du stage partagé reste un parcours de scan.
Dans `.azuredevops/deploy-apps.yml`, les rapports sont produits dans le job Build
et publiés dans un autre job. La variable de sortie `ReportSourceRoot.path`
transmet la racine du checkout producteur à l'entrée `reportSourceRoot` de
`UpCoopPublishReports@1`. `sourceRoot` reste le checkout du job de publication
pour le contexte Git ; le parseur des rapports utilise la racine du producteur
pour convertir les chemins absolus Cobertura en chemins relatifs au dépôt. Ces
deux jobs peuvent recevoir des répertoires Azure Pipelines distincts.
L'[architecture cible des pipelines et scanners](pipelines-scanners-codeinsights.md)
détaille les tâches Azure DevOps, les artefacts .NET 10/React/Flutter et la séparation
entre production, conservation et publication des rapports.

Le contrat version 1 est `POST /api/v1/ingestions`, suivi des uploads bruts
`PUT /api/v1/ingestions/{id}/reports/{report_id}`, du POST `/finalize` et du GET du
reçu. SARIF 2.1.0, Cobertura, LCOV, CycloneDX JSON 1.4–1.7 et SPDX JSON 2.3 sont
pris en charge. Lizard XML est accepté sous le format natif `lizard` version `1` ;
sa colonne fichier `CCN` alimente la complexité. Le manifeste déclare les producteurs attendus, leur état, les
empreintes et le contexte Git. Une absence n'est jamais remplacée par zéro.
Les chemins absolus SARIF, Cobertura, LCOV ou Lizard ne sont acceptés que
lorsqu'ils sont strictement contenus dans le `source_root` absolu déclaré ; ils
sont alors stockés comme chemins relatifs au dépôt. Pour SARIF, LCOV et Lizard,
tout chemin extérieur reste rejeté. Pour Cobertura, une classe (`<class
filename="...">`) dont le chemin tombe hors du `source_root` — par exemple
`reportgenerator` fusionnant la couverture de plusieurs projets ou lecteurs —
laisse seulement cette classe non mesurée (aucune ligne, branche ni complexité
publiée pour elle) au lieu de rejeter tout le rapport ; observé en production
sur un rapport réel de `Up.France.ODI.Services.Financeur.BMF`.
Pour Cobertura, les compteurs `branches-valid` et `condition-coverage` constituent
la même observation native : CodeInsights publie donc `branch_coverage` et
`condition_coverage` avec le même numérateur/dénominateur et conserve cette
provenance dans le détail.
Lorsque Lizard est présent, sa mesure fichier `CCN` est la source de complexité ;
la valeur `complexity` de Cobertura n'est utilisée qu'en fallback. Les deux valeurs
ne sont jamais additionnées.
TRX et JUnit version `1` sont également acceptés. Ils alimentent les métriques
normalisées `tests_total`, `tests_failed`, `tests_skipped` et
`test_duration_ms`. Ces métriques restent propres au parcours d'ingestion et ne
modifient pas le corps strict de `POST /api/v1/analyses`.
Un JUnit sans aucun `<testsuite>` (ex. jest-junit sur un dépôt sans fichier de
test) est un résultat « zéro test » valide, pas un rapport invalide : il produit
`tests_total: 0`, comme TRX l'accepte déjà avec zéro `UnitTestResult`. Un rejet
sur ce cas précis bloquait l'ingestion entière (`technical_state: failed`), pas
seulement la métrique de tests — corrigé après l'avoir observé sur la première
analyse d'`Up.France.ODI.Uptimise.Web`.

Les originaux sont conservés en Blob Storage ; PostgreSQL assure reçu, révisions,
normalisation et jobs durables. La même publication rejouée conserve son identité ;
les contributions complémentaires enrichissent la même analyse. Le gate suit la
finalisation et le traitement, puis les retours ADO/notifications ont leurs propres
jobs. Le client échoue sur `unknown`, échec technique, état incomplet ou timeout.

La même identité projet/provider/dépôt/build/commit et le même manifeste
retrouvent le même reçu. Une contribution modifiée crée une révision de la même
analyse, conserve les rapports complémentaires précédents et réutilise les blobs
identiques. Branche et commits source/cible ne peuvent pas changer entre
contributions ; une révision ancienne terminant après une nouvelle ne remplace pas
sa synthèse. `GET /api/v1/ingestions/{id}` rend le reçu, l'état technique, le gate,
les erreurs, les rapports et le lien d'analyse ; `GET …/reports/{report_id}` rend
l'original autorisé, puis HTTP 410 après purge ; `GET /api/v1/ingestions/queue`
donne aux administrateurs l'état des jobs par type.

Les limites par défaut sont 100 rapports par manifeste, 50 MiB par rapport
(`Ingestion__MaxReportBytes`) et 250 MiB par lot (`Ingestion__MaxManifestBytes`).
L'upload est lu par blocs de 64 KiB ; les parseurs bornent profondeur et volume
mais lisent le rapport en mémoire, avec au plus 250 000 entrées normalisées et un
timeout de parsing de 120 s (`Ingestion__ParseTimeoutSeconds`). Le XML est lu sans
DTD ni résolveur externe. Les archives compressées et les références externes à
télécharger ne font pas partie du contrat.

Pour les SBOM, le graphe fourni détermine `root`, `direct`, `transitive` ou
`unknown`. Les liens CVE proviennent exclusivement du rapport CycloneDX : aucun
catalogue de vulnérabilités n'est interrogé. Un SPDX ou un SBOM sans vulnérabilité
ne démontre pas une absence de CVE ; la mesure reste non mesurée. La décision de
licence compare les valeurs déclarées sans évaluer les expressions SPDX composées ;
toute valeur non couverte exige une revue ([politique de licences](licenses.md)).

La maintenance horaire passe en échec les publications non finalisées et les
analyses `running` depuis plus de 24 heures. Lorsqu'un stockage de rapports est
configuré, elle purge par lots de 100 les détails des ingestions terminées depuis
plus de 90 jours et supprime les blobs qui ne sont plus référencés ; synthèses,
snapshots, identités d'analyse et [décisions de traitement](collaboration.md) sont
conservés. Ce que l'import conserve de chaque format est décrit dans
[Preuves des rapports](report-evidence.md).

Les [routes](../apps/api/Endpoints/IngestionEndpoints.cs) et
[DTO](../apps/api/Contracts/IngestionContracts.cs) sont la source exacte.

Avant de produire le diff, un publicateur peut appeler
`POST /api/v1/new-code/resolve` avec le projet, le dépôt, la branche, la version
et le contexte PR. La réponse donne la définition effective et le snapshot de
baseline exact. L'ordre est cible PR, `new_code_reference_branch` du manifeste,
définition branche, projet, globale, puis `previous_version`. La création de
l'ingestion fige cette résolution et exige que `target_sha` lui corresponde ;
la finalisation renvoie 409 si la configuration ou la baseline a dérivé entre
les deux appels. Les mutations sous `/api/v1/new-code` sont réservées aux
administrateurs et auditées ; les lectures projet appliquent les grants usuels.
`/new-code/resolve` exige `analysis:write`, comme la création d'ingestion. Pour
le déploiement progressif seulement, une ingestion issue de l'ancien producteur
peut conserver son `target_sha` explicite sous la source
`legacy_manifest_target` si aucune configuration dédiée, override ou PR ne
s'applique et si ce SHA correspond déjà à une analyse terminée du même dépôt.
Comme `POST /api/v1/ingestions`, `/new-code/resolve` auto-enregistre un
`project_key` encore inconnu lorsque le jeton CI appelant porte
`project:register` : le corps accepte alors `ci_provider` (`azure-devops` par
défaut), `profile_id` et `profile_version`, obligatoires pour ce cas précis
(422 sinon). Le jeton appelant reçoit l'accès au projet nouvellement créé. Ce
n'est plus le premier appel d'ingestion qui provisionne un nouveau projet :
c'est celui des deux (`/new-code/resolve` ou `/api/v1/ingestions`) qui arrive
en premier dans la séquence du publicateur.

Piège opérationnel observé en production : la garde d'enregistrement
(`NewCode.ValidateAsync`, [NewCode.cs](../apps/api/Services/NewCode.cs))
refuse seulement qu'une règle `reference_branch` de niveau **projet** cible
la `main_branch` déclarée du projet (422 « A project cannot use its main
branch as its own New Code reference »). Elle n'empêche pas de cibler une
autre branche qui possède elle-même sa propre CI. Une règle projet s'applique
à toutes les branches sans définition dédiée — y compris la branche ciblée.
À la résolution (`NewCode.ResolveAsync`), la garde runtime
(`effective.reference_branch == branch` → 422 « A branch cannot use itself as
its New Code reference ») rejette alors l'ingestion de cette seule branche,
silencieusement, jusqu'à ce qu'elle exécute sa propre CI. Cas réel : un projet
dont la branche principale est `develop` ne peut pas enregistrer une règle
projet `reference_branch=develop` (bloquée au 422 de sauvegarde), alors que
« comparer chaque branche à develop » est la configuration la plus naturelle.
Le contournement en production (`Up.France.ODI.Loyalty/ci/azure-pipelines.yml`)
fixe l'override manifeste par build plutôt qu'une définition enregistrée :
chaque build hors `develop` envoie le paramètre pipeline
`codeinsights_new_code_reference_branch: develop` (relayé en
`new_code_reference_branch` dans le manifeste, priorité juste sous la cible
PR — voir `SelectEffective`) ; le build `develop` omet ce paramètre et
retombe sur la définition globale. Quand un override manifeste est fourni,
les définitions enregistrées ne sont même pas lues (`definitions = []` dans
`ResolveAsync`) : il gagne systématiquement, sans dépendre du niveau branche,
projet ou global. Ce même mécanisme se retrouve dans
[Configuration New Code](interface.md).

## Validation et activation

L'onboarding production des dépôts pilotes et la remise en état après une
réinitialisation de la base passent par la pipeline manuelle
`.azuredevops/provision-sast-projects.yml`. Ce provisioning reste la seule
façon d'obtenir un nouveau jeton CI, ou de câbler la service connection
`CodeInsights-Coop` sur un projet Azure DevOps qui ne l'a pas encore. Une fois
cette connexion en place, tout nouveau `project_key` publié par son pipeline
s'auto-enregistre dès le premier appel `/new-code/resolve` ou
`/api/v1/ingestions` (le jeton partagé porte `project:register`), sans repasser
par cette pipeline. Elle restaure et active le fournisseur
Entra depuis le secret Key Vault `codeinsights-entra-client-secret`, enregistre CodeInsights avec
`upcoop-dotnet/2`, les deux applications web avec `upcoop-react/1`, StoreLocator Data avec
`upcoop-python/1` et MSC avec `upcoop-dotnet/2`, puis génère un jeton limité à
`analysis:read`, `analysis:write`, `integration:write` et `project:register`. Sa
valeur est stockée dans le secret Key Vault `codeinsights-ci-token`, puis copiée
dans la service connection `CodeInsights-Coop` de chaque projet Azure DevOps
ciblé. Elle n'est ni affichée ni versionnée et les pipelines consommateurs ne la
reçoivent pas comme variable. Le compte Entra du pilote retrouve son
rôle administrateur et un accès aux cinq projets sans suppression de ses
attributions existantes. Cette remise en état n'est appliquée qu'après sa première
reconnexion Entra, lorsque son identité existe de nouveau dans la base.
Lors du premier passage, la valeur Entra est fournie uniquement comme variable
secrète de queue `CodeInsightsEntraClientSecret` ; la connexion de service la
place dans Key Vault pour les exécutions suivantes. Elle ne doit jamais être
ajoutée au YAML, aux logs ou à une variable non secrète.

Ce secret sert également aux appels Microsoft Graph en `client_credentials` avec le scope `.default`. L'application Entra `60fa4790-a8dd-4565-80c3-8854e1ff163c` doit conserver les permissions Application `User.Read.All` et `GroupMember.Read.All` avec consentement administrateur. Les scopes `openid profile email` restent réservés au flux OIDC utilisateur. Aucun secret ni jeton Graph ne transite par le navigateur ou la pipeline de publication.

Les liens d'intégration projet et la connexion d'organisation Azure DevOps ont des
rôles distincts. Le lien dépôt, enregistré automatiquement par la pipeline ou
corrigé dans Administration > Integrations, sert aux liens source exacts, à la
correspondance des imports et à l'autorisation des retours de pull request. La
pipeline de provisionnement conserve pour chaque dépôt son projet Azure DevOps :
les quatre dépôts ODI utilisent `Up.France.ODI`, tandis que MSC utilise
`Up.France.Services.MSC` et sa définition 991.
La frontière API transforme l'URL de clone Azure Repos
`https://organisation@dev.azure.com/organisation/projet/_git/depot` en
`https://dev.azure.com/organisation/projet/_git/depot` avant stockage et ingestion.
Elle applique la même normalisation au champ `repository.remoteUrl` renvoyé par
l'API des pull requests Azure DevOps avant la comparaison stricte avec le manifeste.
Le nom placé avant `@` doit correspondre à l'organisation du chemin ; les mots de
passe, tokens, query strings, fragments, hôtes non Azure DevOps et chemins ambigus
sont refusés avec un statut 422. Le provisioning réenregistre ces URLs canoniques
et les URLs stables des définitions de build à chaque exécution. Il reteste aussi
chaque configuration Azure DevOps dont le Quality Gate est activé afin de
réenregistrer l'identité technique associée à l'identité managée ; une identité
absente de l'organisation ou insuffisamment autorisée fait échouer le provisioning
avant la rotation du jeton CI.
L'identité managée système de l'App Service est ajoutée explicitement à
l'organisation `up-group`, dans le même tenant Entra, avec un accès Azure DevOps
Basic. Ses permissions sont limitées à la lecture des projets et dépôts configurés,
à la contribution aux pull requests et à la publication de statuts. Lorsque la
capacité `work_items_enabled` est activée, elle reçoit en plus **Work Items (Read &
Write)** sur les projets qui en créent ([work items](collaboration.md#work-items-azure-boards)). CodeInsights
obtient un jeton court pour le scope
`https://app.vssps.visualstudio.com/.default` et l'envoie en Bearer ; aucun secret
Azure DevOps n'est lu ou enregistré par le contrat courant. L'URL d'organisation
est limitée à `https://dev.azure.com/<organisation>`, sans identifiants, query,
fragment, port personnalisé ni segment supplémentaire, avant toute acquisition
de jeton.
La connexion d'organisation est facultative pour recevoir les analyses,
mais nécessaire pour publier le statut Quality Gate, mettre à jour les
commentaires Azure DevOps et créer des work items. Leur absence n'empêche pas la publication par le jeton
CI. L'écran Integrations réunit ces deux configurations sans proposer de
fournisseur ALM non implémenté.
Le `POST /analyses` historique reste compatible, mais sa décoration rejoint désormais le même outbox durable que l’ingestion.
Voir les [budgets](performances.md).

La correction exceptionnelle de `upcoop-dotnet/2` ne recalcule aucun historique.
Elle invalide une fois la baseline antérieure ; le premier build `master`
terminé avec le catalogue corrigé devient la nouvelle référence.

La pipeline de déploiement de CodeInsights délègue au wrapper partagé
`az-dotnet-core-codeinsights.yml` le restore, le build et la production des
rapports .NET et sécurité. Elle désactive les tests génériques du wrapper
(`tests_enabled: false`) et exécute une seule fois `dotnet test` sur
`tests/CodeInsights.Tests` dans ses étapes spécifiques, avec PostgreSQL, Azurite
et l'API démarrés. La publication
CodeInsights est différée : après le déploiement, le stage d'analyse télécharge
les deux artifacts produits pendant le build et appelle une seule fois le
publicateur partagé dans un répertoire dédié, distinct des sorties du stage
`Build`, afin de ne jamais importer deux fois un même rapport sur un pool qui
réutilise le workspace. Elle consomme le tag partagé `v3.1.0` et l'extension privée
`UpCoop Code Quality` `0.5.0`. La
configuration de `Up.France.CodeInsights` utilise `upcoop-dotnet/2` en mode
informatif. Une seule ingestion regroupe les
rapports du conteneur et ceux de l'agent : le SARIF Roslyn couvre le projet API,
dont les règles figurent dans le catalogue `upcoop-dotnet/2`. Les tests xUnit
restent exécutés et publiés avec leur couverture, sans importer leurs diagnostics
xUnit non catalogués comme des défauts de l'API. La réussite du build ne déduit
jamais zéro bug. Sur `master`, la pipeline transmet comme baseline New Code le dernier commit
antérieur ayant touché ses chemins de déclenchement. Elle ignore ainsi les
commits intermédiaires de documentation, d'extension VS Code ou d'autres
pipelines qui n'ont pas déclenché cette analyse. Pour une pull request, le
publicateur résout la dernière analyse terminée de la branche cible avant de
calculer le diff ; la pipeline ne force pas le SHA du parent du commit de fusion.
Le commit choisi sur `master` reste seulement éligible au déclenchement : si sa
publication a échoué ou a été annulée, aucune baseline complétée n'est
disponible. Ce SHA n'est retenu que par le chemin `legacy_manifest_target` :
dès qu'une définition New Code globale, projet ou branche existe, c'est elle qui
choisit la baseline ; un SHA transmis différent de la baseline résolue fait
refuser l'ingestion (409).
Un build de tag `v*` est la première analyse de sa ref `refs/tags/<tag>` : une
définition limitée à la même branche (`previous_version`, `number_of_days`) n'y
trouve jamais de baseline. La pipeline transmet donc `newCodeReferenceBranch` au
publicateur avec le tag `v*` précédent (`git describe --tags --abbrev=0 HEAD^`)
et ne fixe pas `PLATFORM_TARGET_SHA`, pour que le publicateur résolve la
baseline par `/new-code/resolve`. Le New Code d'une release couvre ainsi ce qui
la sépare de la précédente ; sans tag précédent analysé, le gate reste `Unknown`
(`reference_branch_baseline_absent`). Une analyse sans baseline complétée
conserve un gate `Unknown`, mais
reste techniquement complète lorsque tous les producteurs Overall et leurs rapports
sont mesurés ; elle peut alors amorcer la comparaison suivante après revalidation
du profil exact et de la configuration. `dotnet-coverage` mesure l'API pendant
l'unique exécution des tests PostgreSQL/Azurite/HTTP.
Quand son Cobertura issu de MTP ne contient que les lignes exécutables, la
métrique `coverage` reste calculable à partir de ces lignes ; les métriques de
branches et de conditions restent indisponibles. La pipeline transmet la version
du CLI réellement utilisé via `DOTNET_COVERAGE_VERSION`, conservée comme
provenance du rapport. jscpd mesure les blocs
dupliqués, cloc les lignes et Lizard la complexité
cyclomatique. Les images de scanners sont figées par digest.
La tokenisation `.razor` de jscpd 5 peut renvoyer une plage de lignes inversée
(`end < start`) sur de très courts fragments dupliqués ; cette paire précise
est ignorée sans faire échouer le reste du rapport jscpd ni inventer une plage
corrigée.

`license_violations` compte les composants refusés par la politique de licences :
celle du profil s'il en porte une, sinon la politique d'organisation, figée dans
l'ingestion. Un composant sans licence ou une licence non listée reste en décision
`review`, et la mesure reste alors non mesurée : la présenter comme zéro violation
serait trompeur ([politique de licences](licenses.md)).

## Cycle explicite, sources et décorations

Le cycle recommandé est `POST /api/v1/analyses/start`, transfert optionnel des sources, puis `POST /api/v1/analyses/{id}/complete` ou `/fail`. `provider_run_id` rend le start idempotent; une complétion identique est rejouable et un résultat terminal différent renvoie 409. Les écritures exigent `analysis:write`.

Les sources portant une issue se déclarent par `POST /api/v1/analyses/{id}/sources`, puis se transfèrent par `PUT .../sources/{source_id}`. Seuls les chemins relatifs sûrs et le texte UTF-8 sont acceptés. `GET /api/v1/issues/{occurrence_id}/prompt` exige une session autorisée et extrait 20 lignes avant/après. Une source absente ou purgée rend le prompt indisponible sans changer le gate.

Les producteurs continuent d'analyser le dépôt complet afin de conserver des
métriques Overall, rapports de couverture, duplications, dépendances et baselines
comparables. La décoration d'une PR est plus étroite : elle ne retient que les
findings situés sur les lignes ajoutées du `git_diff` publié. Si ce diff est absent
ou incohérent avec les SHA cible et analysé, aucun commentaire ou annotation de
finding n'est envoyé ; le statut de l'analyse et son lien restent publiés.

Azure DevOps utilise le contexte stable `CodeInsights/quality-gate`, nettoie seulement les commentaires des threads gérés écrits par l'identité managée testée, puis republie au plus 49 issues avant le statut final. À partir de 50 findings éligibles, un cinquantième thread non ancré indique le nombre omis et mène à l'analyse complète dans CodeInsights. GitHub utilise une GitHub App, un Check Run par analyse, `external_id=analysisId`, au plus 49 annotations et `annotations_count` pour reprendre un envoi incertain. En cas de troncature, le résumé final porte le même décompte et le lien CodeInsights ; les annotations déjà attachées à un ancien Check Run ne sont pas supprimées.
La décoration Azure DevOps vérifie toujours le SHA source vivant de la PR. Elle
sélectionne ensuite sa dernière itération source, car Azure DevOps peut conserver
un `targetRefCommit` d'itération antérieur après l'avancement de la branche cible.
