---
title: Présentations
description: Supports visuels datés qui expliquent un lot livré ; la documentation de domaine reste la référence.
---

# Présentations

Ce répertoire regroupe des supports visuels à partager, par exemple pour une revue de
PR ou une démonstration. Chaque présentation est **datée** : elle décrit un lot au
moment de sa livraison et n'est pas mise à jour ensuite. En cas d'écart, le
document de domaine indiqué fait autorité.

| Présentation | Date | Contenu | Référence |
| --- | --- | --- | --- |
| [SLA par sévérité](sla-par-severite.html) | 29 septembre 2026 | Lot P5.1 en 14 slides : décisions, modèle de données, calcul et statuts, rattrapage des findings existants, recalcul, API, notifications, interface, fichiers touchés, vérifications et limites | [Traitement des issues](../collaboration.md#délais-de-correction-sla) |
| [Work items Azure Boards](work-items-azure-boards.html) | 29 septembre 2026 | Lot P5.2 en 12 slides : décisions, flux, données, idempotence et refus, contenu du work item, règles automatiques, API, interface, fichiers touchés, vérifications et limites | [Traitement des issues](../collaboration.md#work-items-azure-boards) |
| [Tendances de portefeuille](tendances-portefeuille.html) | 29 septembre 2026 | Lot P5.3 en 10 slides : décisions, flux, valeurs prouvées, reconstruction de l'historique, API filtrée par les droits, interface, fichiers touchés, vérifications et limites | [Tendances de portefeuille](../portfolio.md) |
| [Politique de licences](politique-licences.html) | 29 septembre 2026 | Lot P5.6 (licences) en 10 slides : décisions, flux, classement exact, classification initiale, mesurabilité et inventaire, API, fichiers touchés, vérifications et limites | [Politique de licences](../licenses.md) |
| [Analyses de pull request](analyses-pull-request.html) | 1er octobre 2026 | Séparation des builds de branche et de PR en 9 slides : constat et décisions, fonctionnement, API, interface, fichiers touchés, vérifications, compatibilité et limites | [Intégration](../integration.md#analyses-de-branche-et-de-pull-request) |
| [Analyse des projets PHP](analyse-php.html) | 12 octobre 2026 | Profil `upcoop-php/1` et wrapper AzurePipelines en 8 slides : contexte et décisions, fonctionnement, profil, fichiers touchés, vérifications, ordre de déploiement et limites | [Pipelines et scanners](../pipelines-scanners-codeinsights.md#pipeline-cible-php) |
| [Ratings MQR PHP](ratings-mqr-php.html) | 12 octobre 2026 | Passage de `upcoop-php/1` en MQR en 6 slides : contexte, catalogue, fichiers, vérifications et limites | [Ratings MQR](../mqr-ratings.md#catalogue-upcoop-php1) |

## Ajouter une présentation

Toute PR `feat` en ajoute une ([AGENTS.md](../../AGENTS.md)) : nouvelle capacité ou
changement de comportement observable. Un correctif, une PR de documentation seule ou
une tâche technique n'en demande pas.

- **Fichier** : `docs/presentations/<feature>.html`, nom en kebab-case, et une ligne
  dans le tableau ci-dessus, avec sa date et le document de domaine qui fait
  référence.
- **Format** : reprendre la structure de [sla-par-severite.html](sla-par-severite.html).
  Page autonome, slides 16:9 au canevas 1920×1080 mis à l'échelle, notes dépliables
  sous chaque slide, styles en ligne, aucun script externe ni dépendance autre que
  Google Fonts.
- **Contenu, dans cet ordre** : contexte et décisions ; fonctionnement ; surfaces
  modifiées (données, API, interface, notifications, selon la feature) ; fichiers
  créés et modifiés ; vérifications, en séparant ce qui a réellement tourné, avec
  les résultats observés, de ce qui n'a pas été vérifié ; limites et suite.
- **Règles** : rien que ce que le code et les tests établissent. Une mesure absente
  n'est jamais présentée comme un succès. Aucun secret, jeton ni donnée de
  production. Tous les liens internes doivent être résolus.

## Consulter

Chaque fichier `.html` est autonome : l'ouvrir dans un navigateur affiche les slides
au format 16:9, mises à l'échelle de la fenêtre, avec leurs notes dépliables. Les
polices sont chargées depuis Google Fonts et remplacées par Arial hors ligne.
L'impression produit une slide par bloc, sans les notes.
