---
title: Preuves des rapports
description: Ce que l'import conserve des rapports SARIF, couverture, duplication, taille et complexité, et comment ces preuves sont fusionnées et liées au code.
---

# Preuves des rapports

Le hub importe des résultats existants ; il n'exécute aucun scanner. Le contrat des
formats acceptés, `source_root` et les règles propres à Cobertura et Lizard sont
dans [Intégration](integration.md#parcours-multi-rapports) ; cette page décrit ce
qui est conservé et comment les preuves sont combinées. Les métadonnées normalisées
sont limitées à 250 000 entrées et 50 Mio UTF-8 par ingestion et disparaissent
avec ses détails ; le rapport original reste la preuve complète, soumis à sa
rétention et à ses droits d'accès.

## Findings SARIF

L'import SARIF conserve les positions principales, les localisations associées
et les étapes des `codeFlows.threadFlows`, avec leurs index de flux et de thread.
Les régions conservent lignes et colonnes lorsqu'elles sont renseignées. Aucun
snippet de code n'est copié dans les métadonnées. La limite est de 200 positions
par résultat ; `locations_truncated` indique le dépassement. Le budget de 50 Mio
est partagé par toutes les occurrences et tous les rapports d'une ingestion : un
dépassement échoue explicitement, pour qu'une occurrence multi-position répétée ne
puisse pas amplifier sans limite les métadonnées persistées.

La documentation provient exclusivement de `tool.driver.rules[].helpUri` pour
la règle correspondante. Un lien HTTPS sans identifiants est conservé avec la
provenance `sarif.tool.driver.rules.helpUri` et l'état `producer_link_unverified` :
CodeInsights n'a vérifié ni sa disponibilité réseau, ni la propriété de son domaine.
Aucun accès externe, cache ou contenu éditorial n'est ajouté. Les descripteurs
d'extensions non résolus ne sont pas associés à une documentation de règle du
driver par homonymie. La version de l'analyseur provient de `semanticVersion`,
sinon `version` ; elle ne constitue pas une version documentaire, et
`documentation_version` reste inconnu. Une version de producteur dépassant 200
caractères n'est pas conservée. Une règle inconnue ou un lien absent/non sûr
produit `unavailable`, sans modifier le résultat d'import ni le verdict du gate.

## Liens de code

Les liens de fichier Azure DevOps exigent un dépôt HTTPS reconnu (`dev.azure.com`
ou organisation `visualstudio.com`), un chemin relatif et un SHA complet de 40 ou
64 caractères hexadécimaux. Ils ciblent le commit de la publication et la ligne
fournie, sans repli sur HEAD ou une branche mutable. Les URI contenant des
identifiants, une query ou un fragment ne sont pas reprises pour le dépôt. Une
localisation avec `uriBaseId` non résolu ne produit pas de lien : la relation avec
la racine du dépôt n'est pas prouvée.

Les chemins absolus ne sont convertis en chemins relatifs que s'ils sont
strictement enfants du `source_root` déclaré : racine Windows `C:/agent/source`,
POSIX `/agent/source` ou URI locale `file:///...`. Les chemins Windows se comparent
sans distinction de casse, les chemins POSIX avec. Un préfixe voisin
(`source-other`), une traversée même encodée, une URI UNC, une query ou un fragment
sont refusés. Sans racine déclarée, un chemin absolu reste refusé. Aucune racine
n'est devinée et le rapport natif stocké demeure inchangé. Le source affiché dans
l'interface est lu à la demande au SHA exact ([architecture](architecture.md)).

## Couverture

| Format / version de l'adaptateur | Données conservées |
| --- | --- |
| `lcov` / `1` ou `1.0` | `DA` pour les lignes ; `BRDA` pour les branches identifiées par fichier, ligne, bloc et branche. `BRF` doit correspondre aux détails présents. |
| `cobertura` / `1` ou `1.0` | Lignes de classe ; `condition-coverage="50% (1/2)"` conserve les deux entiers, jamais le pourcentage arrondi. `branches-valid`, lorsqu'il existe, doit correspondre aux détails. |

Les lignes sont réunies par fichier/ligne, couvertes si au moins un rapport les
couvre. Les branches LCOV du même producteur/version sont réunies par identité
exacte et couvertes par union des hits positifs. Les numérateurs et dénominateurs
sont ensuite additionnés ; aucune moyenne de pourcentages n'est calculée.

Pour Cobertura, `branches-valid` et `condition-coverage` sont une même observation,
publiée à la fois comme `branch_coverage` et `condition_coverage` avec le même
numérateur et dénominateur ([intégration](integration.md#parcours-multi-rapports)).
Ces conditions ne donnent pas l'identité de chaque résultat booléen : deux rapports
superposés sur une même ligne sont `ambiguous`, même avec des comptages égaux, car
leur union ne peut pas être prouvée. Des versions de producteurs différentes sur la
même branche sont également ambiguës.

## Duplication, taille et complexité

| Format / version de l'adaptateur | Production et données conservées |
| --- | --- |
| `jscpd` / `1` | JSON natif `jscpd --reporters json ./src` : `duplicates[]`, `firstFile` et `secondFile` avec `name/start/end`, langage, lignes et tokens. Les fragments source sont ignorés. `tool_version` du manifeste est obligatoire. |
| `cloc` / `1` | JSON natif `cloc --by-file --json --out=cloc.json src` : entrée par chemin avec `language` et `code`. `header.cloc_version` doit correspondre exactement au `tool_version` du manifeste. Les sorties agrégées par langage ne sont pas acceptées. |
| `lizard` / `1` | XML natif ; la colonne fichier `CCN` alimente la complexité. `tool_version` est obligatoire. |

Les versions `1` identifient l'adaptateur, pas la version du moteur. Chaque rapport
conserve outil, version de l'outil et identifiant du rapport. Références natives :
[JSON jscpd](https://jscpd.dev/reporters/json) et [options cloc](https://github.com/AlDanial/cloc).

Les blocs dupliqués restent un compteur de blocs prouvés, sans pourcentage
artificiel ni déduction à partir des code smells ; des rapports jscpd de
producteurs ou versions différents rendent la duplication ambiguë. La taille est
le nombre de lignes de code déclaré par cloc. La complexité vient de Lizard
lorsqu'il est présent ; la `complexity` des classes Cobertura n'est utilisée qu'en
repli, et les deux valeurs ne sont jamais additionnées. Des valeurs contradictoires
pour un même élément produisent une mesure inconnue. Taille et complexité restent
globales : leurs rapports n'ont pas de répartition par ligne pour le nouveau code.

## États

`not_measured` signale une dimension non fournie ; `not_applicable` un ensemble
mesuré sans élément applicable ; `unavailable` une comparaison Git indisponible ;
`ambiguous` des preuves impossibles à fusionner. Aucun de ces états ne vaut 100 %,
et une métrique absente ne satisfait jamais une condition de gate. Les anciens
résultats ont des preuves vides : aucune documentation ni localisation n'est
inventée. Aucun score ou équivalence avec SonarQube n'est créé ; la classification
des règles et les modèles de remédiation appartiennent au [profil](profiles.md).

Ces preuves sont consultables dans le détail d'une issue et dans la sous-vue
`Code quality details` de Projects ([interface](interface.md)). Les tests
`FindingEvidenceTests`, `CodeQualityTests` et `ReportImportTests` couvrent provenance,
positions et flux, absence de snippets, liens non sûrs, SHA abrégé, hôte trompeur,
URI base non résolue, troncature, union LCOV, ambiguïté Cobertura, comptages cloc,
duplications réelles et rejets des chemins et valeurs invalides.
