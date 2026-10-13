---
title: Performances et capacité du hub
description: Risques vérifiés, priorités d'optimisation et protocole de charge pour plusieurs centaines de projets.
---

# Performances et capacité du hub

## État implémenté et limites à mesurer

| Surface                | État implémenté                                                    | Limite résiduelle                                                                                |
| ---------------------- | ------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------ |
| Historiques d'analyse  | Projections paginées ; courbes ramenées à 32 points représentatifs | Tester les filtres/indices sur les volumes A/B/C ; le downsampling reste local à la page chargée |
| Findings et composants | Tables normalisées et détails paginés ; rapports hors des listes   | Mesurer les plans SQL et la densité réelle des rapports                                          |
| Ingestion              | Upload Blob borné et queue PostgreSQL durable avec baux/retries    | JSON en mémoire jusqu'à 50 MiB, 250 000 entrées normalisées par lot ; EF AddRange sans bulk COPY |
| Retours externes       | Jobs durables PR/notifications de l'ingestion, hors transaction d'import | Livraison externe au moins une fois ; verrous par PR pendant les appels ADO                      |
| Publication directe    | Contrat POST conservé, mesures et gate ; décoration par job durable `pr-decoration` | Notifications en mémoire, distinctes des jobs durables de l'ingestion                            |
| Rétention              | Maintenance horaire durable et conservation des synthèses          | Lots de 100 détails ; mesurer retard de purge et scan des objets orphelins                       |
| Coût CI                | Une image packagée produit sécurité, duplication, taille et complexité ; Roslyn/couverture réutilisent le SDK et le build agent | Le template .NET relance les projets pour produire un SARIF déterministe ; mesurer ce surcoût avant une optimisation MSBuild |
| Chargement du SPA      | Écrans métier/admin en chunks Vite ; requêtes activées par vue ; profils chargés à la première ouverture | Sur le build local du 15 septembre 2026, bundle d'entrée de 120,82 à 79,13 Ko gzip ; mesurer le temps interactif sur le réseau cible |

Les parcours `upcoop-dotnet/1` et `/2` ne publient qu'une fois : l'extension fusionne les
rapports packagés et les rapports .NET téléchargés depuis l'artefact du stage
Build. Cette organisation évite une seconde attente d'ingestion et les collisions
d'identifiants entre deux publishers. Les tests sont exécutés une seule fois avec
PostgreSQL, Azurite et l'API ; `dotnet-coverage` attache la collecte au processus API
pendant cette même passe. Le cache Trivy reste séparé des rapports.

Le découpage frontend conserve le splash de démarrage existant et ne modifie pas le contrat visuel. Les gros écrans sont téléchargés au premier usage ; leurs chunks restent ensuite pris en charge par le cache navigateur. Les options des sélecteurs de projets sont mémorisées et la recherche d'une sélection utilise un `Set`, sans virtualisation tant que les volumes réels ne la justifient pas.

Les sources sont les [routes de consultation](../apps/api/Endpoints/InsightEndpoints.cs),
le [worker](../apps/api/Services/IngestionWorker.cs), le
[processeur](../apps/api/Services/IngestionProcessor.cs).
Une borne de requête ne prouve pas à elle seule un
SLO sous concurrence.

La configuration Terraform B1/B1ms est un point de départ, **pas une capacité validée pour 20 analyses simultanées ni pour plusieurs centaines de projets**. Le nombre de projets seul ne détermine pas la charge : fréquence, historique, concurrence et densité de findings comptent davantage.

## Mesures et optimisations restantes

1. Compléter les mesures de parsing/normalisation/SQL et livraison externe ; corréler build, ingestion, révision et analyse aux durées client déjà journalisées.
2. Vérifier plans SQL et indices sur les filtres projet/branche/date/état ; mesurer aussi catalogue projets et projets possédant beaucoup de branches.
3. Mesurer uploads, consommation mémoire des parseurs et cadence de la queue avec le plafond 50 MiB/rapport et 250 MiB/lot ; évaluer bulk COPY seulement si le coût EF mesuré le justifie.
4. Vérifier purge, reprises de baux et effets externes sous panne ; dimensionner SKU/pools à partir des mesures, avant tout cache supplémentaire.

Budget SQL : `répliques × processus × plafond du pool + préparation du schéma + réserve exploitation` doit rester inférieur au plafond exploitable PostgreSQL. Le conteneur API démarre un processus .NET ; des chaînes de connexion distinctes peuvent créer des pools supplémentaires. Vérifier le plafond réel sur l'environnement de test ; ajouter une réplique web multiplie les pools, pas la capacité de la base.

Les tests décrits dans [exploitation](exploitation.md#développement-local) vérifient le fonctionnement. Le protocole de charge ci-dessous sert à mesurer la capacité.

## Protocole de charge hors production

Ces volumes sont des **jeux de test proposés**, pas la volumétrie constatée de l'organisation. Générer des données fictives et des rapports valides représentatifs dans une base isolée ; ne pas copier des sources ou secrets métier.

| Palier | Projets | Analyses conservées par projet | Rapports par analyse | Publications simultanées | Lecteurs simultanés |
| ------ | ------: | -----------------------------: | -------------------: | -----------------------: | ------------------: |
| A      |     100 |                            100 |                    3 |                        5 |                  10 |
| B      |     300 |                            300 |                    3 |                       20 |                  30 |
| C      |     500 |                            500 |                    5 |                       50 |                  50 |

Pour chaque palier, varier indépendamment taille de rapport (1, 10, 50 Mo), densité (100, 10 000, 100 000 findings) et concentration sur un projet. Ne pas remplir tous les rapports historiques au volume maximal par défaut : comparer une distribution réaliste puis une rafale défavorable. Les rapports au-delà d'une limite configurée doivent être rejetés explicitement ; le contrat accepte au maximum 50 MiB par rapport, sous réserve des bornes de parsing et du total de 250 MiB par lot. Une taille acceptée ne préjuge pas de sa latence.

## Budgets initiaux à valider

| Mesure                                  | Objectif provisoire                                              |
| --------------------------------------- | ---------------------------------------------------------------- |
| API de synthèse projet/portefeuille     | p95 ≤ 1 s, page bornée, sans rapport brut                        |
| Affichage interactif du portefeuille    | p95 ≤ 2 s sur poste/réseau de test documentés                    |
| Acceptation après transfert du rapport  | p95 ≤ 2 s ; distinguer durée réseau et normalisation             |
| Rapport de 10 Mo jusqu'au gate final    | p95 ≤ 60 s au palier B, pour un format/profil documenté          |
| Erreurs techniques sous charge nominale | < 1 %, aucune perte silencieuse ni doublon                       |
| Reprise après rafale                    | Retard de file résorbé en ≤ 10 min après retour au débit nominal |

Mesurer aussi p50/p99, débit soutenable, backlog/âge maximal, mémoire navigateur/API/worker, CPU, I/O et verrous SQL, connexions, taille des réponses et octets transférés. Publier le coût Azure de la campagne et le coût par analyse ; ne pas extrapoler un tarif ou un SLO de production sans budget approuvé.

## Scénarios de résistance et critères de sortie

- Republier le même lot, puis un rapport complémentaire : ni doublon ni verdict prématuré.
- Arrêter le worker pendant le parsing, rétablir la base et simuler 429/timeouts ADO : reprise bornée et état consultable.
- Envoyer un document invalide, compressé excessivement ou hors limite : rejet explicite sans bloquer les autres projets.
- Purger des détails pendant les consultations : synthèses et décisions collaboratives conservées, latences mesurées.
- Vérifier l'équité entre un gros dépôt et plusieurs petits ; surveiller connexions et mémoire au scale-out.

### Outil de campagne

[scripts/p1-load.py](../scripts/p1-load.py) exécute ce protocole avec Python
standard, sur une API **loopback exclusivement**, reliée à une base PostgreSQL de
développement isolée. Le jeton administrateur est transmis par
`CODEINSIGHTS_LOAD_TOKEN` ; aucun jeton n'apparaît dans le journal.

Créer une nouvelle base/container et une API migrée par palier. Le peuplement
`--seed-fixtures` requiert `--db-container` explicite et refuse toute base qui
contient déjà un projet ou une analyse ; il ne supprime aucune donnée. Les projets
sont créés via l'API, puis PostgreSQL insère des **synthèses historiques**
déterministes, explicitement incomplètes et sans findings historiques inventés.
Les nouveaux rapports passent entièrement par le contrat d'ingestion : manifeste,
upload, finalisation et attente du traitement terminal.

```powershell
# Injecter CODEINSIGHTS_LOAD_TOKEN depuis le secret de la session locale.
python scripts/p1-load.py --url http://127.0.0.1:8000 --tier A --seed-fixtures --db-container codeinsights-load-a --db-name codeinsights --output .artifacts/p1-load-a.jsonl
# Smoke court sur une autre base vide
python scripts/p1-load.py --url http://127.0.0.1:8000 --smoke --seed-fixtures --db-container codeinsights-load-smoke --duration 10 --warmup 2 --burst 2 --repeats 1 --report-bytes 4096 --density 10 --output .artifacts/p1-load-smoke.jsonl
```

Les défauts sont 300 s de chauffe, 1 800 s stables et 300 s de rafale, répétés
trois fois ; la rafale double les producteurs et garde le même nombre de lecteurs.
Chaque palier dure donc au moins deux heures, plus peuplement et drainage.
`--seed` fixe les projets, historiques et choix des lecteurs/producteurs ;
`--report-bytes` fixe une taille minimale par rapport et `--density` le nombre de
findings SARIF. Les rapports synthétiques mesurent l'ingestion, pas la variété des
analyseurs ; les matrices de taille et de densité se lancent séparément.

Le journal JSONL, sous `.artifacts/` ignoré par Git, contient les paramètres, la
progression, p50/p95/p99 des lectures et du parcours complet de publication, le
débit, les erreurs et les publications non terminales. Avec `--db-container`,
l'état des jobs durables est lu dans PostgreSQL toutes les cinq secondes. Les
percentiles portent sur les opérations réussies ; un gate `unknown` sur le profil
synthétique n'est pas un échec technique, `state=failed` en est un. CPU, mémoire,
I/O, connexions et coût Azure se mesurent séparément : le runner ne les invente pas.

Le lot sera accepté après campagne complète reproductible, comparaison aux budgets,
explication des dépassements et absence de perte de données. Le smoke local
(PostgreSQL/Azurite/API .NET) a réussi 75 publications et 6 064 lectures : p95 stable
publication 1,187 s, lecture 0,032 s. Il emploie 2 projets, 2 synthèses historiques
par projet, 3 rapports de 4 096 octets et 10 findings par rapport. Il ne valide
aucun palier A/B/C.

**La campagne A/B/C standard n'a pas été exécutée**, ni la matrice complète de
formats/tailles/densités, ni la campagne de résilience distante. Les chiffres du
smoke ne justifient aucune extrapolation de SKU, de coût ou de capacité Azure.
