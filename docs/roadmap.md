---
title: Roadmap produit du hub
description: Acceptations en attente, extensions à décider et écarts restants avec la vision cible de chaîne d'audit qualité et sécurité.
---

# Roadmap produit du hub

## Ambition

Offrir aux développeurs un résultat actionnable dans leur PR, aux équipes qualité et
sécurité un traitement partagé des problèmes et aux responsables une progression
fiable de leurs projets. La cible porte sur quelques centaines de projets Azure
DevOps et agrège les moteurs existants ; elle ne consiste pas à réécrire les
analyseurs SonarQube ni à en revendiquer la parité.

Cette page ne liste que ce qui reste à accepter, à décider ou à réaliser. Les
efforts sont relatifs — S = changement ciblé, M = plusieurs parcours/composants,
L = lot transversal — et ne constituent pas un engagement calendaire.

## Déjà livré

Les capacités suivantes sont implémentées ; leur document fait autorité :

- accès, identités et jetons : [autorisations](security-access.md) ;
- publication directe et ingestion multi-rapports durable : [intégration](integration.md),
  [pipelines et scanners](pipelines-scanners-codeinsights.md) ;
- preuves des rapports, couverture, duplication, taille et complexité :
  [preuves des rapports](report-evidence.md) ;
- profils versionnés, catalogue, configurations natives, périmètres :
  [profils](profiles.md) ; ratings A–E : [Ratings MQR](mqr-ratings.md) ;
- gate sur le nouveau code : [nouveau code](new-code.md) ;
- tendances de portefeuille : [tendances de portefeuille](portfolio.md) ;
- politique de licences : [licences](licenses.md) ;
- traitement des issues, délais de correction (SLA), work items Azure Boards et revue sécurité : [traitement des issues](collaboration.md),
  [revue sécurité](security-review.md) ;
- retours dans l'IDE et pour les agents : [extension VS Code](vscode-extension.md),
  [extension Visual Studio](visualstudio-extension.md), [serveur MCP](mcp-server.md).

## Acceptations en attente

La livraison logicielle ne vaut pas acceptation organisationnelle. Restent à
exécuter, avec leurs preuves :

| Acceptation | Critère de sortie |
| --- | --- |
| Pilote Azure DevOps .NET/React/IaC | Statut rattaché au bon commit et à la bonne itération, mise à jour sans doublon, détails des nouveaux problèmes, droits réels de l'identité managée ; validation de la branch policy sur un dépôt de test autorisé. Le mode informatif précède tout mode bloquant, décidé par les responsables de projet. |
| Campagne de charge A/B/C | Protocole de [performances](performances.md) exécuté de façon reproductible, comparé aux budgets, dépassements expliqués, aucune perte de données. Le smoke local ne valide aucun palier. |
| Scénario de bout en bout | Une PR introduit un défaut détecté par une règle connue ; le développeur ouvre la bonne ligne au bon commit, comprend le refus du gate, corrige et republie ; la PR passe et l'historique conserve les deux résultats. Variantes : scanner absent, baseline introuvable, déplacement de fichier, dérogation expirée, accès interprojets refusé. |
| Documentation de règle officielle | Sur une règle documentée du pilote, le lien du producteur mène à l'explication et aux conseils de correction ; règle inconnue, lien absent et version différente restent explicites, sans contenu inventé ni incidence sur le gate. |

Les critères UX transversaux comprennent clavier, focus visible, libellés explicites,
états de chargement/erreur, tableaux accessibles en alternative aux courbes et
présentation adaptée aux petits écrans. Aucun audit WCAG complet n'a été réalisé.

## Extensions à décider avec les équipes

Ces lots ne sont pas des prérequis. Valider leur usage et le coût des moteurs avant
de les engager ; réutiliser les intégrations natives des outils existants avant de
développer une extension ou un analyseur.

| Fonctionnalité / valeur | Dépendances et effort | Acceptation / limite |
| --- | --- | --- |
| Analyse sémantique et flux avancés | Évaluation des moteurs et licences ; L | Mesurer sur un corpus .NET/React/IaC les vrais/faux positifs et temps de scan ; importer les traces des moteurs retenus. Étendre la [matrice des langages couverts](pipelines-scanners-codeinsights.md#langages-et-stacks-couverts) aux types d'analyse sémantique ; un import SARIF seul ne démontre pas une capacité d'analyse interprocédurale. |
| Réévaluation SCA et politiques SBOM | SBOM conservés ; L | Rafraîchir les CVE sur les SBOM conservés, tracer source/date et version de base, notifier une nouvelle exposition sans rebuild. Évaluer expressions SPDX, versions corrigées et avis VEX avec provenance ; aucun statut « non affecté » déduit sans preuve. Conserver le verdict initial. |
| Corrélation inter-outils | Identité durable des findings ; L | Relier les détections d'un même défaut en conservant chaque preuve et producteur ; règles de regroupement auditées, séparation possible, compteurs explicables. L'aide à la correction existe déjà en partie (prompt d'issue, outil MCP `explain_finding`) ; une suggestion IA reste une proposition soumise à revue et à validation par les outils. |

Une éventuelle vue « application » agrégeant plusieurs dépôts n'est engagée que si
les équipes en ont besoin, avec périmètre versionné et sans moyenne qui masque un
projet critique.

## Analyses de pull request

Les lectures de branche, la synthèse, les baselines New Code, les tendances, les
findings durables, les extensions et le serveur MCP séparent déjà builds de branche
et builds de PR
([intégration](integration.md#analyses-de-branche-et-de-pull-request),
[traitement des issues](collaboration.md#analyses-de-pull-request)). Le report
d'une décision prise sur une PR vers la branche cible après fusion n'est pas prévu.
Reste :

| Lot | Dépendances et effort | Acceptation / limite |
| --- | --- | --- |
| Rétention des PR fermées | Statut de PR Azure DevOps et GitHub ; M | Purger les détails d'une analyse de PR 30 jours après la fermeture de sa PR (décision prise), séparément du quota `retention_max_per_branch`. Aujourd'hui, les builds de PR partagent ce quota avec les builds de leur branche source, et une branche protégée (`develop`) ne purge jamais ses builds de PR par l'âge. Détecter la fermeture demande d'interroger le fournisseur ou de recevoir son événement. |

## Vision cible outillée

La vision cible décrit une chaîne d'audit en quatre étapes : commit outillé, analyse
CI qualité et sécurité, quality gate sur des rapports normalisés, pilotage.
CodeInsights occupe déjà la place de consolidation et de triage que cette vision
attribue à DefectDojo. Les lots ci-dessous comblent les écarts restants sans
remplacer le hub ni son moteur de gate.

| Fonctionnalité / valeur | Dépendances et effort | Acceptation / limite |
| --- | --- | --- |
| P5.0 Documentation par domaine | Aucune ; S | Réalisé le 29 septembre 2026 : documents par lot et journaux remplacés par des documents de domaine vérifiés contre le code ; aucun lien interne cassé. |
| P5.1 SLA par sévérité | Livré le 29 septembre 2026 ([traitement des issues](collaboration.md#délais-de-correction-sla)) ; M | Reste à accepter sur un pilote : politique réelle validée par les responsables sécurité, statuts et résumés de dépassement observés sur un canal Teams ou email réel. Un finding sans date de détection prouvée reste `indeterminate`, jamais « dans les temps ». |
| P5.2 Work items Azure Boards | Livré le 29 septembre 2026 ([work items](collaboration.md#work-items-azure-boards)) ; M à L | Reste à accepter sur un pilote : droit Work Items (Read & Write) accordé à l'identité managée, work item réel créé puis commenté, règles observées sur une analyse réelle. |
| P5.3 Tendances de portefeuille | Livré le 29 septembre 2026 dans le hub ([tendances](portfolio.md)) ; M | Reste à accepter sur un pilote : courbes lues par les responsables sur plusieurs semaines d'analyses réelles, et coût de la maintenance horaire mesuré sur le volume réel de projets. Choix d'une vue dans le hub plutôt que Grafana ou Power BI, pour respecter les droits par projet. |
| P5.4 Hooks pre-commit | Templates `Up.France.ODI.AzurePipelines` et onboarding ; S | Configuration par stack avec `gitleaks` et les formateurs, versions alignées sur les images CI. Les hooks restent un conseil contournable ; la CI conserve le verdict. |
| P5.5 Politiques de gate versionnées dans Git | Profils immuables et routes de publication existantes ; M | Définitions de profils revues par PR, validées puis publiées par la CI ; une version publiée ne peut pas changer de contenu ; provenance (dépôt, commit) visible dans le hub. Le moteur de gate reste unique : pas de second évaluateur de politique. |
| P5.6 Scanners d'appoint | Politique de licences livrée le 29 septembre 2026 ([licences](licenses.md)) ; S à M | Reste à faire : validation juridique de la classification initiale ; OSV-Scanner seulement si un écart avec Trivy est prouvé ; CodeQL soumis à GitHub Advanced Security sur du code privé ; stack Java seulement sur besoin d'une équipe. |

Ordre recommandé pour la suite : P5.5, puis le reste de P5.6 selon les besoins ;
P5.4 en parallèle, dans le dépôt des templates.

## Livraison et critères transversaux

Pour chaque lot : contrat documenté, tests ciblés, pilote sur projets représentatifs
(.NET, React et IaC), puis déploiement progressif via Azure DevOps et Terraform.
Chaque capacité est activée après validation du pilote.

La validation couvre notamment : import répété, rapports complémentaires d'un build,
scanner absent, format invalide, accès interprojets refusé, redémarrage worker,
429/timeout ADO, conservation des rapports après gate échoué, progression réelle et
rétention. Les objectifs de disponibilité, de restauration et le budget Azure seront
chiffrés avec l'exploitation à partir des mesures du pilote.
