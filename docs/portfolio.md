---
title: Tendances de portefeuille
description: Snapshots quotidiens de la branche principale des projets, ce qu'ils prouvent, leur agrégation par jour et la vue Portfolio trends.
---

# Tendances de portefeuille

La vue **Portfolio trends**, en tête de **All Projects** dans Projects, suit dans le
temps les projets que l'utilisateur est autorisé à voir. Elle ne contourne pas les droits
par projet : l'API n'agrège que les projets accordés à l'appelant. Aucun outil externe
ni aucune infrastructure n'est ajouté. Sources :
[PortfolioSnapshots.cs](../apps/api/Services/PortfolioSnapshots.cs) et
[PortfolioEndpoints.cs](../apps/api/Endpoints/PortfolioEndpoints.cs).

## Snapshots quotidiens

La table `portfolio_snapshots` contient une ligne par projet et par jour UTC. Elle décrit
la **branche principale** (`projects.main_branch`) telle que la montre la dernière
analyse terminée créée avant la fin de ce jour. Un projet sans branche principale, ou
sans analyse terminée sur celle-ci, n'a pas de ligne ; la réponse de l'API compte les
projets sans branche principale.

| Valeur | Source | Absente quand |
| --- | --- | --- |
| Ratings Security, Reliability, Maintainability, Security review | Colonnes de l'analyse retenue | Le profil ne calcule pas de ratings MQR |
| Couverture et duplication | Pourcentages entiers de l'analyse, comme l'historique d'un projet | Le producteur n'a pas mesuré |
| Findings par sévérité | Occurrences de la dernière révision terminée de l'analyse, hors exceptions de profil, comptées comme dans Issues ; une sévérité inconnue reste `unknown` | Les détails de l'analyse ont été purgés |
| Findings SLA `breached` et `due_soon` | Findings durables de la branche principale ([SLA](collaboration.md#délais-de-correction-sla)) | Le jour n'a pas été observé en direct |

Une valeur absente reste `null` : les agrégats l'ignorent au lieu de la compter comme
zéro, et les courbes s'interrompent.

La maintenance horaire recalcule la ligne du jour pour chaque projet (`source = live`).
La dernière ligne d'un jour décrit donc l'état au dernier passage de ce jour. Les jours
manquants, jusqu'à 400 jours en arrière et à partir de la première analyse du projet,
sont reconstruits depuis les analyses conservées (`source = reconstructed`) : ratings,
couverture et duplication sont repris, les findings par sévérité seulement si les
détails existent encore, les SLA jamais, car leur état passé n'a pas été enregistré.
Au plus 50 projets sont reconstruits par passage ; les autres suivent aux passages
suivants.

## API

`GET /api/v1/insights/portfolio?days=90` (7 à 365 jours, 90 par défaut) exige une session
ou un jeton personnel et agrège les projets autorisés de l'appelant.

- `days[]` : par jour, le nombre de projets ayant une ligne ; pour chaque rating, le nombre
  de projets par lettre et non notés ; `findings` et `sla` avec le nombre de projets
  mesurés et les sommes, ou `null` si aucun projet n'est mesuré ce jour-là ; le nombre de
  projets dont la couverture et la duplication sont mesurées.
- `projects[]` : pour chaque projet, sa dernière ligne de la période, avec la couverture
  et la duplication du premier jour de la période pour mesurer l'évolution.
- `projects_without_main_branch` : projets autorisés exclus faute de branche principale.

Aucune moyenne n'est calculée : une moyenne masquerait un projet critique. La couverture
et la duplication se lisent projet par projet.

## Interface

La section propose une période de 30, 90, 180 ou 365 jours et quatre onglets :

- **Ratings** : pour la dimension choisie, le nombre de projets en A, B, C, D, E et non
  notés, jour par jour ;
- **Findings** : somme par sévérité sur les projets mesurés ;
- **SLA** : dépassements et échéances proches ; tant qu'aucun jour n'a été observé en
  direct, un message l'explique au lieu d'une courbe à zéro ;
- **Coverage & duplication** : tableau par projet, valeur la plus récente et évolution
  en points sur la période, du projet le moins couvert au plus couvert. Une évolution
  sans valeur de départ mesurée s'affiche `-`.

L'infobulle d'une courbe indique le nombre de projets mesurés ce jour-là. Les courbes
reprennent le rendu de l'historique d'un projet : un tracé et une couleur par série, et
une interruption à chaque absence de mesure. Sans snapshot, la section annonce que les
tendances apparaissent après le prochain passage de maintenance.

## Limites

- Un jour décrit l'état au dernier passage horaire de ce jour, pas un instant précis.
- Les SLA passés ne peuvent pas être reconstruits : leur courbe commence au déploiement.
- Les snapshots ne sont pas purgés ; leur volume reste d'une ligne par projet et par jour.
- La vue porte sur la branche principale ; les autres branches n'y figurent pas.

`PortfolioTests` couvre la sélection de l'analyse du jour, la reconstruction, les détails
purgés, le SLA observé en direct, le budget de reconstruction et l'agrégation filtrée par
droits.
