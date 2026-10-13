---
title: Profils et périmètres
description: Profils d'ingestion versionnés, catalogue de règles, configurations natives, activation des règles, estimation de remédiation et périmètres de code.
---

# Profils et périmètres

Un profil d'ingestion décrit, dans une définition immuable par identifiant et
version, les outils et mesures attendus, la classification des règles, les
conditions de gate, la politique de [revue sécurité](security-review.md) et les
configurations natives des analyseurs. L'affectation d'un profil à un projet et son
mode (`informative` ou `blocking`) sont administrés par les routes
`/api/v1/profiles` et audités. Le profil `upcoop-dotnet/2` et ses ratings sont
décrits dans [Ratings MQR](mqr-ratings.md) ; les conditions `new_code` dans
[Nouveau code](new-code.md). Sources : [ProfileEndpoints.cs](../apps/api/Endpoints/ProfileEndpoints.cs)
et [ExecutableProfiles.cs](../apps/api/Services/ExecutableProfiles.cs).

## Catalogue de règles

La définition conserve un `rule_catalog` indépendant des occurrences. Aucun texte
d'explication ou conseil éditorial n'est rédigé dans CodeInsights. Une règle décrit
`tool`, `tool_version`, `rule_id`, `language`, `category`, `parameters` (objet),
un lien HTTPS `documentation_url` et une `documentation_version` facultatifs.
L'identité fonctionnelle d'une règle est `(tool, rule_id)` : `tool_version` est une
provenance obligatoire, pas une clé. Le lien provient du producteur ; sa présence ne
prouve ni sa disponibilité ni sa correspondance avec la version exécutée. La
classification (`bug`, `code_smell`, `vulnerability`, `security_hotspot`, `secret`
ou `unknown`) appartient à cette version de profil.

## Configurations natives

Une configuration décrit `tool`, `tool_version`, `format`, `path` relatif et
`content`. Trois formats natifs sont acceptés : `editorconfig`, `semgrep-json`
et `eslint-flat-json`. Les deux premiers conservent les octets UTF-8 fournis ;
le dernier encapsule le tableau JSON en `module.exports = <tableau>;` dans un
fichier `.cjs` consommable par ESLint. Cette voie couvre les règles ESLint natives ;
plugins et parsers nécessitant du code exécutable restent configurés dans le dépôt.
Le service vérifie la structure, les bornes et les chemins ; l'analyseur reste
l'autorité de validation sémantique de sa configuration et doit réussir dans la CI.
Les configurations sont limitées à 100 fichiers et 1 MiB UTF-8 par profil,
le catalogue à 10 000 règles. Les chemins sont relatifs, sans traversée,
identifiants de disque ni doublons insensibles à la casse. Les contenus
exportés ne sont jamais exécutés par le serveur.

Références : [EditorConfig .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-files),
[configuration ESLint](https://eslint.org/docs/latest/use/configure/configuration-files),
[règles Semgrep](https://semgrep.dev/docs/writing-rules/rule-ideas).

## Contrats

- `POST /api/v1/profiles` publie une nouvelle version, réservée aux administrateurs.
- `GET /api/v1/profiles/{id}/versions/{version}/catalogue` fournit des règles
  paginées (50, maximum 100), filtrables par `search` et `language`. Aucune occurrence
  préalable n'est nécessaire ; le catalogue ne prétend pas que la règle a tourné.
- `GET /api/v1/profiles/projects/{key}/configuration` exporte le profil affecté,
  ses fichiers natifs et SHA-256, avec empreinte globale. L'accès au projet est vérifié.
- `GET /api/v1/profiles/{id}/versions/{version}/diff/{previous}` compare règles,
  conditions, outils et empreintes de deux versions.
- `GET /api/v1/profiles/projects/{key}/impact?profileId=…&version=…` prévisualise
  ce changement avant l'affectation. Cette comparaison ne rejoue pas les analyses
  et ne prétend pas prédire leurs verdicts.
- `PUT /api/v1/profiles/projects/{key}` affecte une version publiée. Choisir
  une ancienne version réalise le retour arrière ; aucune version n'est réécrite.
- `allowed_licenses` et `denied_licenses`, lorsqu'une définition en porte, priment sur la
  politique de licences d'organisation ([politique de licences](licenses.md)).
- `GET` et `POST /api/v1/profiles/projects/{key}/exceptions` gèrent les exceptions
  de profil, avec motif et expiration. Profil et exceptions actives sont figés à la
  création de l'ingestion ; une reprise ne relit pas des exceptions modifiées entre-temps.

## Activation des règles par projet

Une définition peut porter `disabled_rules`, liste d'identités
`{tool, tool_version, rule_id}` comparées sur `(tool, rule_id)`. L'absence de cette
propriété équivaut à une liste vide. La commande administrative
`POST /api/v1/profiles/projects/{key}/rule-activation` exige l'identité de la règle,
l'état demandé et l'identifiant/version attendus du profil affecté. Un changement
concurrent d'affectation répond `409`.

La commande clone la version immuable, publie une version
`<version-base>-rules.<n>` puis l'affecte au projet dans la même transaction. La
désactivation met une règle ESLint à `off`, retire la règle de l'export Semgrep ou
ajoute une surcharge `dotnet_diagnostic.<id>.severity = none` en fin
d'EditorConfig. La réactivation repart toujours de la configuration source de la
version parente : elle ne détruit donc pas la valeur d'origine. Une règle absente
du catalogue ou un format qui ne peut pas être transformé sûrement est refusé.
Les analyses et verdicts déjà enregistrés ne sont jamais réécrits.

## Configuration effective

La CI consomme l'export avec les analyseurs déjà présents : `.editorconfig` dans
la portée du projet .NET, `semgrep scan --config <fichier.json>` ou
`eslint --config <fichier.cjs>`. L'export ne lance aucun de ces programmes. Pour
`upcoop-dotnet/2`, la tâche `UpCoopSecurityScan@2` apporte elle-même le ruleset
Semgrep `upcoop-dotnet-mqr/v1` ; le dépôt consommateur ne porte aucun fichier
`.codeinsights` ([architecture](architecture.md)). Les paramètres de publication
sont documentés par le template partagé ([intégration](integration.md)).

Le manifeste d'ingestion déclare `effective_configurations` : outil, version,
chemin et SHA-256 calculé sur les **octets réellement présents**, même s'ils
divergent de l'export. La présence d'un fichier ne certifie pas à elle seule son
utilisation par le scanner. Le serveur compare l'empreinte attendue à l'empreinte
effective et produit :

- `not_configured` lorsque le profil ne porte aucune configuration native ;
- `missing` lorsque le manifeste ne déclare aucune configuration effective, ou
  qu'un analyseur configuré n'a pas de rapport mesuré ;
- `matched` ou `drift` selon l'égalité des deux empreintes.

L'empreinte globale est le SHA-256 UTF-8 de la concaténation des fichiers triés
par chemin ordinal, chacun donnant `tool\npath\nsha256_minuscule\n`. La version
d'outil reste une provenance et n'entre pas dans l'empreinte. Les instantanés
conservent la dérive ; aucune correction de profil ne modifie un verdict passé.

## Estimation de remédiation

Une règle peut fournir `remediation: {model: "constant", duration_seconds: N,
documentation_url: "https://…"}` lorsque ce modèle vient du producteur.
La durée doit être positive, au maximum un an, avec sa source HTTPS.
L'agrégation résout chaque finding par `(tool, rule_id)` ; sans modèle elle
reste non mesurée. Une somme partielle annonce combien de findings sont
modélisés et non modélisés, ses sources, le profil et `producer_constant_seconds/v1`.
Il s'agit d'une estimation en secondes, sans équivalence avec une note SonarQube.

## Périmètres de code

Les périmètres de composant sont des versions immuables par projet, appliquées
par le worker avant les agrégats et figées dans chaque import. L'API n'étend pas
un import selon la configuration courante du projet. Ils ne sont plus modifiables
depuis l'interface ; l'API reste disponible pour les administrateurs, et les
lecteurs autorisés conservent la consultation
([CodeEndpoints.cs](../apps/api/Endpoints/CodeEndpoints.cs)) :

- `GET /api/v1/code/projects/{key}/versions` liste les versions publiées, paginées ;
  `POST …/versions` en publie une nouvelle ;
- `GET …/configuration` lit l'affectation et `PUT …/configuration` affecte une
  version avec `{ "version": "…" }` ; réaffecter une ancienne version permet le
  retour arrière ;
- `GET …/branches` liste les branches observées, paginées ; `PUT …/branches` avec
  `{ "branch": "refs/heads/…", "archived": true }` archive une branche. C'est un état
  de classement réversible : aucune suppression Git, analyse ou décision n'est déclenchée.

Une version précise dépôt HTTPS, composant et branche principale. Les listes de
chemins utilisent **un fichier exact ou un préfixe de répertoire terminé par `/`**,
pas des globs. Une inclusion vide inclut tout le dépôt ; les exclusions sont
prioritaires. Les listes tests, généré et tiers identifient ces périmètres
séparément. Les chemins sont relatifs, sans traversée, au maximum 200 entrées par
liste. Le manifeste explicite `code_scope_version`, la cible Git et l'inventaire
des chemins au commit analysé, limité à 100 000 chemins.

Les champs de référence (branche ou analyse) d'une version restent obligatoires
et lisibles pour les configurations historiques, mais **ne choisissent plus la
baseline New Code** : celle-ci vient des définitions décrites dans
[Nouveau code](new-code.md).

## Vérification

`ExecutableProfileTests` couvre export natif, hash exact, absence, dérive de
contenu, traversée de chemins et durée partielle ; `CodeScopeTests` les périmètres.
L'acceptation en production exige les analyseurs réels configurés, puis leurs
rapports publiés sur un pilote.
