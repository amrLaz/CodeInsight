---
title: Politique de licences
description: Classement des composants SBOM par une politique de licences d'organisation figée dans chaque ingestion, et calcul de license_violations.
---

# Politique de licences

Chaque composant d'un SBOM CycloneDX ou SPDX est classé `allowed`, `denied` ou `review`
selon une liste de licences autorisées et une liste de licences refusées. La mesure
`license_violations` compte les composants `denied`. Sources :
[LicensePolicies.cs](../apps/api/Services/LicensePolicies.cs) et
[LicenseEndpoints.cs](../apps/api/Endpoints/LicenseEndpoints.cs).

## Quelle politique s'applique

- Si le [profil](profiles.md) affecté porte ses propres listes (`allowed_licenses`,
  `denied_licenses`), elles s'appliquent.
- Sinon, c'est la **politique d'organisation**, unique pour tous les projets.

Le choix est figé dans l'ingestion à sa création (`license_policy_snapshot`, avec sa
source et sa version). Une reprise ou un rejeu juge donc les licences avec la politique du
jour de la publication, et modifier la politique ne réécrit aucun verdict passé. Une
ingestion créée avant cette politique reste jugée par les seules listes de son profil.

## Classement

Un composant est :

- `denied` si l'une de ses licences est refusée ;
- `allowed` s'il déclare au moins une licence et qu'elles sont toutes autorisées ;
- `review` sinon, notamment s'il ne déclare aucune licence ou une licence absente des
  deux listes.

La comparaison porte sur la chaîne déclarée par le SBOM — CycloneDX `expression`, sinon
`license.id`, sinon `license.name` ; SPDX `licenseDeclared`. Elle est **exacte et
sensible à la casse**, et les expressions SPDX ne sont pas évaluées : `MIT OR Apache-2.0`
doit être listée telle quelle, et un nom libre comme `Apache License 2.0` ne correspond
pas à `Apache-2.0`. L'évaluation des expressions est prévue dans la roadmap (P4.3).

`license_violations` n'est calculée que si tous les SBOM du manifeste sont mesurés et
qu'**aucun composant n'est en `review`**. Sinon elle reste non mesurée : présenter zéro
violation serait trompeur tant qu'un composant n'est pas classé.

## Classification initiale

La migration crée la politique d'organisation suivante. Elle suit la pratique courante et
doit être **validée par le juridique**.

| Liste | Licences |
| --- | --- |
| Autorisées (permissives) | `MIT`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause`, `ISC`, `0BSD`, `Zlib`, `BSL-1.0`, `Unlicense`, `CC0-1.0`, `PostgreSQL`, `Python-2.0` |
| Autorisées (copyleft faible) | `LGPL-2.1-only`, `LGPL-2.1-or-later`, `LGPL-3.0-only`, `LGPL-3.0-or-later`, `MPL-2.0`, `EPL-2.0`, et les formes dépréciées `LGPL-2.1`, `LGPL-2.1+`, `LGPL-3.0`, `LGPL-3.0+` |
| Refusées | `GPL-2.0-only`, `GPL-2.0-or-later`, `GPL-3.0-only`, `GPL-3.0-or-later`, `AGPL-3.0-only`, `AGPL-3.0-or-later`, `SSPL-1.0`, `BUSL-1.1`, `CC-BY-NC-4.0`, et les formes dépréciées `GPL-2.0`, `GPL-2.0+`, `GPL-3.0`, `GPL-3.0+`, `AGPL-3.0` |

Le copyleft faible est autorisé pour des dépendances utilisées sans modification, y
compris dans les VSIX et le paquet npm distribués.

## API et administration

- `GET /api/v1/licenses/policy` : politique d'organisation (session ou jeton personnel).
- `PUT /api/v1/licenses/policy` : administrateurs. Porte `version` (409 si elle a changé,
  0 pour la créer), `allowed_licenses` et `denied_licenses`. Les entrées sont épurées des
  espaces et des doublons, limitées à 1 000 par liste et 200 caractères par entrée. Une
  licence présente dans les deux listes est refusée (422). L'écriture est auditée.
- `GET /api/v1/licenses/unclassified` : administrateurs. Inventaire des licences déclarées
  dans la **dernière ingestion terminée et non purgée de chaque projet** que la politique
  ne classe pas, des plus fréquentes aux moins fréquentes (200 au plus), avec le nombre de
  composants et de projets. Les composants sans licence déclarée sont comptés à part :
  aucune liste ne peut les classer.

Administration > Configuration > Licenses édite les deux listes, une licence par ligne,
et bloque l'enregistrement d'une licence présente dans les deux. L'inventaire propose
« Allow » ou « Deny » pour ajouter une licence au brouillon ; l'enregistrement s'applique
dès l'ingestion suivante.

`LicenseTests` couvre le classement exact, la classification initiale, l'épuration des
entrées, la priorité du profil, l'ingestion antérieure à la politique et l'inventaire.
