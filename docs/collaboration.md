---
title: Traitement des issues
description: Identité durable des findings, assignation, décisions motivées, exceptions, continuité après déplacement, délais de correction (SLA) et work items Azure Boards.
---

# Traitement des issues

Le traitement est intégré au détail d'une issue ; l'interface est décrite dans
[Interface](interface.md#suivi-des-issues). Les routes sont dans
[CollaborationEndpoints.cs](../apps/api/Endpoints/CollaborationEndpoints.cs) et la
logique dans [FindingCollaboration.cs](../apps/api/Services/FindingCollaboration.cs).

## Identité durable

Les deux parcours de publication associent les occurrences à un finding
durable dans la transaction du résultat. La clé versionnée `finding/1` contient
le projet, la branche, l'outil et la règle. Un fingerprint unique dans le rapport
normalisé conserve cette identité après un déplacement ou un renommage. Sans
fingerprint exploitable, la correspondance exige le même fichier, la même ligne
et le même message : un déplacement ne transmet donc pas automatiquement les
décisions. Les fingerprints ambigus utilisent cette correspondance conservatrice.
La propriété `identity_kind` rend cette limite visible.

Les occurrences antérieures au suivi durable peuvent être adoptées via
`POST /api/v1/collaboration/occurrences/{id}` depuis le détail Issues. Cette
opération porte sur les occurrences de la même analyse/révision. Les filtres
collaboratifs portent sur les findings déjà suivis, pas sur des anciennes
occurrences encore non adoptées. La liste indépendante des findings conserve
les liens `finding_id` après disparition des rapports.

### Analyses de pull request

Une analyse de pull request est rangée sous sa branche source, mais elle analyse ce
que la PR fusionne ([analyses de branche et de pull request](integration.md#analyses-de-branche-et-de-pull-request)).
Elle **relie** ses occurrences aux findings durables existants de la branche source,
sans jamais les modifier : pas de création, pas de réouverture d'un finding résolu,
pas de remise à zéro d'une revue ou d'une exception, pas de mise à jour de la
dernière observation, de la localisation, de la sévérité ni du SLA. Les décisions
prises sur la branche source s'appliquent donc à la PR, et la PR ne réécrit pas le
triage de la branche.

Une occurrence propre au résultat de fusion reste sans finding durable tant que
personne ne la trie. L'ouvrir dans le détail Issues
(`POST /api/v1/collaboration/occurrences/{id}`) crée son finding sur la **branche
source**, pour cette seule occurrence : la décision y est enregistrée et vaut pour
les builds suivants de la branche et de ses PR. Elle n'est pas reportée sur la
branche cible après la fusion, dont le finding reste distinct.

Une analyse de PR n'enregistre aucune correspondance Git d'identité, et sa preuve
n'est jamais retenue comme celle d'un finding de branche : la revue d'un hotspot,
la détection d'une preuve modifiée et la date de première détection ne lisent que
l'occurrence de la dernière observation par un build de branche.

### Correspondances Git

Une empreinte unique du producteur reste la première preuve d'identité. Sans
empreinte exploitable, une correspondance unique de chemin renommé et de ligne
inchangée dans le diff peut conserver l'identité, l'assignation et l'historique.
Le commit de base doit appartenir au même projet et à la même branche que
l'analyse courante ; le commit de tête doit être celui analysé. Aucune
dérogation n'est transférée entre branches.

`finding_identity_matches` conserve les SHA, localisations, méthode et empreinte
de preuve indépendamment des occurrences. Une correspondance déjà validée
reste utilisable aux scans suivants, même si ceux-ci ne fournissent pas de diff.
Les collisions ne choisissent pas arbitrairement une ancienne occurrence : elles
restent `indeterminate`, sans transfert automatique. La conservation d'une
identité n'empêche pas la réinitialisation de sa revue si la preuve change. La
purge des détails ne supprime ni les décisions ni ces correspondances.

## Propriétaires et assignation

Les administrateurs créent les équipes par projet et affectent le propriétaire
et l'équipe par défaut via `/collaboration/teams` et `/collaboration/ownership/{key}`.
Ces valeurs initialisent les nouveaux findings ; changer un propriétaire de
projet ne réassigne pas silencieusement les décisions existantes. Les humains
autorisés sur le projet peuvent assigner un finding à une équipe de ce projet
et à un utilisateur actif disposant d'un accès effectif au projet. Les équipes
sont des périmètres de travail : elles ne confèrent aucun droit d'accès.

Les lectures `/collaboration/people?project_key=…`, `/teams`, `/findings` et
`/findings/{id}/decisions` sont bornées par `limit` (1–100) et curseur.
`mine=true`, `team_id`, `state`, `project_key` et `branch` sont appliqués au
serveur avant pagination. Le GET findings d'une analyse accepte également
`mine`, `team_id` et `state`. Aucun token CI ne peut effectuer une revue humaine ;
un jeton personnel doté du scope `collaboration:write` agit avec les droits exacts
de son propriétaire ([autorisations](security-access.md)).

## Décisions et exceptions

`PUT /collaboration/findings/{id}` remplace l'assignation et la revue avec la
`version` lue. Une version obsolète renvoie 409 ; le client doit relire avant de
réessayer. Les états sont `open`, `confirmed`, `resolved`, `false_positive` et
`accepted_risk`. Tout changement d'état ou de revue exige un motif. Les commentaires
utilisent `POST /findings/{id}/comments`, avec version et `reason` (4 000 caractères
maximum). Les écritures sans changement ne produisent pas de notification.

Une exception (`false_positive` ou `accepted_risk`) est réservée à l'administrateur,
motivée et limitée à un an. Elle ne concerne que cette identité projet/branche.
À expiration, `effective_state` vaut immédiatement `open` ; la maintenance horaire
enregistre la réouverture par lots de 100. Un finding résolu observé dans une
analyse ultérieure est rouvert et audité. L'absence dans un scan incomplet ou sans
comparaison fiable ne ferme jamais automatiquement un finding.

La revue hotspot (`not_reviewed`, `safe`, `to_fix`) est distincte de l'état :
seuls les findings classés `security_hotspot` acceptent une revue autre que
`not_reviewed`. Un changement de catégorie ou de message réinitialise cette
revue et invalide l'exception éventuelle avec une décision auditée.

Ces décisions **ne réécrivent pas les verdicts d'analyse**. Elles n'influencent le
gate que par la politique `security_review` d'un profil, qui fige les décisions
utilisées à chaque publication ([revue sécurité](security-review.md)). Les
exceptions de [profil](profiles.md) restent le mécanisme explicite de politique
du gate. Aucun faux positif global ni suppression de règle n'est créé par cette revue.

Chaque décision conserve acteur, date, motif et instantané dans `finding_decisions`,
sans cascade vers une analyse et sans rétention automatique du journal général.
La purge des rapports conserve assignation et décisions. La suppression explicite
d'un projet reste une opération administrative distincte ; ses anciens findings
ne sont plus accessibles lorsque le projet ne fait plus partie des droits.

## Délais de correction (SLA)

Un finding durable de la **branche principale** de son projet (`projects.main_branch`)
reçoit une échéance de correction ; les autres branches sont hors SLA. La logique est
dans [FindingSla.cs](../apps/api/Services/FindingSla.cs).

**Politique.** Une politique globale fixe un délai en jours par sévérité ; un projet
peut la remplacer entièrement par une surcharge, sans fusion des deux. Un délai vide
signifie « pas d'échéance pour cette sévérité ». La migration crée la politique
globale Blocker 7 j, High 30 j, Medium 90 j, Low 180 j, Info sans échéance. Les
routes sont `GET`/`PUT /api/v1/sla/policy` et `GET`/`PUT`/`DELETE
/api/v1/sla/projects/{projectKey}` ; les écritures sont réservées aux
administrateurs, versionnées (`version` lue, 409 si elle a changé, 0 pour créer une
surcharge), bornées de 1 à 3 650 jours et auditées. La lecture d'un projet renvoie sa
branche principale, sa surcharge, la politique effective et le nombre de findings
suivis par statut SLA.

**Échéance.** Le départ est la date de création de la première analyse contenant le
finding (`first_detected_at`), conservée lors d'une réouverture. La sévérité est
celle de la dernière occurrence, ramenée à `blocker`, `high`, `medium`, `low` ou
`info` ; une sévérité inconnue reste `unknown` et n'est jamais assimilée à `info`.
L'échéance vaut départ + délai ; la période « bientôt dû » couvre le dernier
cinquième du délai, au moins un jour. Pour un finding suivi avant l'existence des
SLA, la date de première détection est reprise de la plus ancienne analyse qui
contient encore l'une de ses occurrences, **seulement** si aucune analyse antérieure
de la même branche n'a perdu ses détails par rétention : sinon la vraie première
détection peut être plus ancienne et la date reste inconnue.

| Statut (`sla.status`) | Signification |
| --- | --- |
| `on_track`, `due_soon`, `breached` | Échéance en cours : avant la période d'alerte, dans celle-ci, dépassée. |
| `suspended` | Une exception active (`false_positive`, `accepted_risk`) suspend l'échéance ; à son expiration l'échéance reprend. |
| `closed` | Finding résolu. |
| `not_applicable` | Branche autre que la principale (`not_main_branch`) ou sévérité sans délai (`no_target`). |
| `not_configured` | Aucune politique ne s'applique (`no_policy`). |
| `indeterminate` | Branche principale, sévérité ou date de détection inconnue (`unknown_*`). |
| `pending` | Échéance en cours de calcul. |

La vue d'un finding expose `severity`, `first_detected_at` et
`sla: {status, basis, due_at, warn_at}`. `basis` donne la cause d'un statut sans
échéance ; aucune date n'est inventée.

**Calcul.** L'échéance est persistée pour que `sla_status` filtre en SQL avant
pagination, sur `/collaboration/findings` comme sur les findings d'une analyse et leur
export CSV ; le statut, qui dépend de l'horloge et de l'état de revue, est dérivé à la
lecture. L'ingestion calcule l'échéance des findings observés. Un changement de
politique ou de branche principale remet les findings concernés en `pending` dans la
même transaction et réveille le job durable `sla`, qui les recalcule par projet et
branche sous le verrou de collaboration ; la maintenance horaire le fait aussi, ce qui
couvre un recalcul demandé pendant qu'un autre s'exécutait et l'initialisation
automatique de la branche principale.

**Notifications.** Un canal webhook ou email reçoit les dépassements lorsque son option
`on_sla_breach` est activée (désactivée par défaut). Chaque passage de maintenance ou du
job `sla` envoie au plus un résumé par projet et par canal, via le job durable
`sla-notification` : le nombre de findings, les 20 plus anciennes échéances et la
mention du reste. Un finding n'est notifié qu'une fois par échéance ; un allongement
de délai qui repousse l'échéance peut le notifier à nouveau. Les dépassements sont
marqués comme notifiés même sans canal actif : activer un canal plus tard n'envoie pas
l'historique. La livraison reste au moins une fois, comme pour les autres effets
externes.

Les SLA ne modifient ni l'état d'un finding ni le verdict du gate.

## Work items Azure Boards

Un finding durable peut ouvrir **un** work item Azure Boards, à la demande ou par des
règles automatiques. La logique est dans
[FindingWorkItems.cs](../apps/api/Services/FindingWorkItems.cs) et le lien dans la
table `finding_work_items`. Contrairement aux projections de PR, rejouables et sans
mapping stocké, un work item est un objet externe que les équipes modifient : son
identifiant est conservé pour éviter un doublon et pour le commenter ensuite.

**Prérequis.** Le projet doit avoir un lien dépôt Azure Repos
(`https://dev.azure.com/<organisation>/<projet>/_git/<dépôt>`), qui désigne le projet
Azure DevOps de destination, et son organisation doit être testée avec la capacité
`work_items_enabled`. L'identité managée a alors besoin du droit **Work Items (Read &
Write)** sur ce projet ([intégration](integration.md#validation-et-activation)). Sans
ces prérequis, la demande est refusée (422) avec la cause, et les règles ignorent le
projet.

**Réglages par projet.** `GET`/`PUT /api/v1/work-items/projects/{projectKey}` : type de
work item (`Bug` par défaut ; `Issue` pour un process Basic), area path et itération
facultatifs, règles automatiques. Les écritures sont réservées aux administrateurs,
versionnées (409 si la version a changé, 0 pour créer les réglages) et auditées. La
lecture renvoie aussi la disponibilité : destination, ou cause de l'indisponibilité.

**Création.** `POST /api/v1/collaboration/findings/{id}/work-item` enregistre la
demande et met en file le job durable `work-item` ; `GET` renvoie le lien, ou `null`
tant qu'il n'en existe pas. Un lien `pending` ou `created` est conservé tel quel ; un
lien `failed` est redemandé. Le work item porte :

- un titre `[CodeInsights] <sévérité> · <outil>/<règle> · <chemin>` (255 caractères au plus) ;
- une description HTML échappée : message, règle, sévérité, catégorie, localisation,
  branche et statut SLA ;
- les tags `CodeInsights` et `codeinsights-finding-<id>` ;
- un lien hypertexte vers l'analyse du finding dans le hub quand `Ingestion:PlatformUrl`
  est configuré.

Azure DevOps ne rend pas la création idempotente : avant de créer, le job cherche en
WIQL un work item portant le tag du finding et l'adopte s'il existe. Une réponse perdue
puis une reprise ne créent donc pas de doublon. Un refus d'Azure DevOps (400, 401, 403,
404) est terminal : le lien passe `failed` avec le message d'Azure DevOps et, pour 401 ou
403, le rappel du droit manquant. Une limitation de débit ou une erreur serveur est
reprise jusqu'à cinq fois, puis le lien passe `failed`.

**Règles automatiques.** Deux règles, indépendantes, portent sur les findings ouverts ou
confirmés de la branche principale et sans lien :

- sévérité minimale : les findings de cette sévérité ou plus, détectés après
  l'activation des règles ;
- dépassement de SLA : les findings `breached` dont l'échéance tombe après
  l'activation.

Activer une règle fixe `auto_since` ; l'historique antérieur ne crée donc aucun work
item. Désactiver toutes les règles efface cette date. À chaque passage, un projet reçoit
au plus `auto_max_per_run` work items (1 à 100, 10 par défaut) : d'abord les dépassements
de SLA, puis les sévérités de la plus haute à la plus basse, les plus anciens d'abord.
Le reste attend le passage suivant. Les règles s'exécutent dans le job `sla`, réveillé à
chaque ingestion qui crée des findings, et dans la maintenance horaire. Un lien en échec
n'est jamais recréé automatiquement.

**Résolution.** Passer un finding à `resolved` met en file le job `work-item-comment`,
qui ajoute au work item un commentaire avec l'auteur, le motif et le lien vers le hub.
CodeInsights ne ferme jamais le work item, et fermer le work item ne change pas le
finding : le scan reste la source de vérité.

Les tests `CollaborationTests`, `SecurityReviewTests`, `SlaTests` et `WorkItemTests` de
`tests/CodeInsights.Tests` couvrent ces contrats sur une base PostgreSQL isolée,
avec un substitut HTTP d'Azure DevOps pour les work items
([exploitation](exploitation.md#développement-local)).
