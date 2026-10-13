---
title: Nouveau code
description: Conditions de gate globales ou limitées au diff Git, comparaison avec la baseline, couverture des lignes modifiées et preuves de calcul.
---

# Nouveau code

Cette page décrit l'évaluation du nouveau code par le parcours d'ingestion. Les
autres aspects sont documentés ailleurs :

- choix et résolution de la baseline (`POST /api/v1/new-code/resolve`, ordre de
  priorité, garde d'auto-référence) : [Intégration](integration.md#parcours-multi-rapports) ;
- définitions globale, projet et branche dans Administration, et vue New Code d'une
  analyse : [Interface](interface.md) ;
- conditions New Code du profil immuable `upcoop-dotnet/2` et ratings A–E :
  [Ratings MQR](mqr-ratings.md).

Le contrat direct `POST /api/v1/analyses` n'évalue pas le nouveau code.

## Conditions `global` et `new_code`

Chaque condition d'un [profil](profiles.md) porte un `scope` : `global` (défaut,
compatible avec les profils existants) ou `new_code`. Les conditions globales et
nouveau code peuvent coexister. Une dette historique n'échoue pas une condition
nouveau code, mais reste soumise à toute condition globale explicite.

Un profil publié accepte le scope `new_code` pour `bugs`, `code_smells`,
`vulnerabilities`, `security_hotspots`, `coverage`, `branch_coverage`,
`condition_coverage`, `duplicate_blocks`, `duplicated_lines` et
`duplicated_lines_density`. Il est refusé pour `dependency_vulnerabilities`,
`license_violations`, `code_lines` et `complexity` : les rapports SBOM, de taille
et de complexité ne fournissent pas de répartition par ligne. Les conditions sur
les ratings (`*_rating`) sont réservées aux profils immuables livrés avec l'API.

## Comparaison avec la baseline

La comparaison précède l'évaluation du gate. Elle utilise les SHA figés du
manifeste, une baseline du même projet et dépôt, le même profil et des producteurs
compatibles : la version d'outil reste une provenance, seule une `format_version`
différente rend deux producteurs incompatibles. L'absence historique du champ
`scope` est normalisée en `global` avant cette comparaison ; elle ne rend pas une
ancienne baseline incompatible à elle seule.

Une analyse de pull request n'est jamais candidate comme baseline, quel que soit le
mode : elle analyse ce que la PR fusionne, pas la branche elle-même
([analyses de branche et de pull request](integration.md#analyses-de-branche-et-de-pull-request)).
Seuls les builds de branche terminés sont retenus.

Le rapprochement classe les findings en `new`, `persistent`, `resolved` et
`indeterminate`. Le résumé détaille au plus 100 findings et indique sa troncature ;
les compteurs utilisés par le gate portent sur toutes les occurrences. Une
baseline absente, purgée ou incompatible donne `unknown`, et un résultat ambigu
rend la dimension concernée inconnue. Aucun résultat global ne remplace une
comparaison indisponible.

## Couverture et duplication des lignes modifiées

La couverture croise les lignes exécutables reçues et les lignes ajoutées du
diff. Les rapports sont fusionnés par `(chemin, ligne)` avec un OU des hits ;
aucune moyenne de pourcentages. Un fichier modifié absent du corpus de couverture
rend la mesure inconnue. Si le corpus est connu mais aucune ligne exécutable
n'est modifiée, la condition est `not_applicable` avec valeur nulle, numérateur
et dénominateur zéro. Une couverture non produite ne devient jamais non applicable.
Le périmètre reste celui des rapports : le hub ne lit pas les sources pour
certifier que le producteur a couvert toutes les lignes exécutables.

Un bloc dupliqué appartient au nouveau code lorsqu'une de ses deux plages
intersecte les lignes ajoutées du périmètre ; la densité rapporte les lignes
dupliquées aux lignes ajoutées. Sans diff, ces valeurs sont inconnues. Le détail
des formats est dans [Preuves des rapports](report-evidence.md).

## Instantané et preuves

L'instantané du gate conserve scope, mesure, opérateur, seuil, résultat, motif,
provenance, baseline et SHA base/head ; pour la couverture, le numérateur et le
dénominateur. Les profils utilisant le nouveau code portent la formule `imports/2`,
les calculs globaux `imports/1` ; les profils MQR utilisent leur propre formule
([Ratings MQR](mqr-ratings.md)). Les résultats historiques ne sont pas réécrits
lors d'une correction ou d'une modification de profil.

Le bloc `Calculation evidence` d'une analyse expose ces champs et l'état non
applicable, distinct d'une réussite et d'une mesure absente. Les exceptions de
profil restent figées à la publication ; les décisions de traitement
n'interviennent que via la politique de [revue sécurité](security-review.md).

## Vérification

`NewCodeGateTests` couvre notamment les comptes au-delà de la troncature,
l'ambiguïté, les scopes invalides, la couverture et les données absentes ;
`NewCodeDefinitionTests` la résolution des définitions. `IngestionTests` exerce le
worker et la persistance via HTTP/PostgreSQL/Azurite : défaut nouveau refusé,
correction acceptée malgré un défaut historique, baseline absente et conservation
du verdict précédent, avec refus interprojets.
