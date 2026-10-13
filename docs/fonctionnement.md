---
title: Fonctionnement et écarts du hub
description: Capacités observées dans le code, limites du signal qualité et écarts vers une expérience Sonar.
---

# Fonctionnement et écarts du hub

## Parcours actuel

Un runner de pipeline transmet une analyse associée à un projet, une branche et un commit. L'API enregistre les métriques, évalue un gate et peut publier un retour Azure DevOps. Le frontend concentre la lecture sur Projects, Issues, Rules et Quality Gates.

Les capacités ci-dessous sont établies à partir du code local. Les tests et leurs limites sont décrits dans [exploitation](exploitation.md#développement-local).

## Lire les ratings et les scores historiques

La [documentation de l’interface](interface.md) décrit les thèmes clair et sombre, les mesures absentes, la lecture seule des gates, le tableau d’évolution et les contrôles locaux.

Le profil `upcoop-dotnet/2` expose les ratings MQR A–E calculés et figés par
l'API ; le détail est dans [Ratings MQR](mqr-ratings.md). Les scores `/100`
subsistent uniquement pour les profils et analyses historiques. Sécurité,
fiabilité, maintenabilité, revue des hotspots, couverture, duplication et SCA
restent des dimensions séparées. Zéro n'est fiable que si l'analyseur attendu a
réellement réussi ; sinon la valeur est explicitement indisponible.

L'historique conserve commit, branche, outils et versions, profil de règles,
périmètre analysé, complétude et version du gate/formule. Les séries A–E sont
distinctes des anciens scores et s'interrompent lorsque la mesure manque.

## Référence Sonar et limites de la comparaison

L'[intégration Azure DevOps de Sonar](https://www.sonarsource.com/integrations/azure/devops/) relie dépôts, analyses de branches et retours PR. Les [quality gates](https://docs.sonarsource.com/sonarqube-server/quality-standards-administration/managing-quality-gates/introduction) expriment une politique de qualité. La cible du hub reprend ces parcours : configuration guidée, signal explicable, nouveau code réel et action depuis la PR.

La parité des moteurs, règles par langage, analyses sémantiques et intégrations IDE n'est pas acquise par l'agrégation des rapports. Les fonctions SBOM/licences constituent aussi une extension propre au hub. Les extensions [VS Code](vscode-extension.md) et [Visual Studio](visualstudio-extension.md) consomment ces rapports ; elles ne recréent pas le moteur.
