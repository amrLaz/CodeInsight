---
title: Revue sécurité
description: Politique de revue des hotspots versionnée dans le profil, décisions figées à la publication et taux de revue.
---

# Revue sécurité

La politique est portée par le profil d'ingestion ([profils](profiles.md)) et
évaluée par [SecurityReviewEvaluation.cs](../apps/api/Services/SecurityReviewEvaluation.cs).
Les décisions elles-mêmes sont prises dans le détail d'une issue
([traitement des issues](collaboration.md)). Pour les profils MQR, la revue
alimente aussi le rating Security review ([Ratings MQR](mqr-ratings.md)).

## Politique versionnée

Le profil d'ingestion porte `security_review` :

```json
{
  "apply_approved_exceptions": true,
  "minimum_hotspot_review_percent": 100,
  "scope": "new_code"
}
```

Par défaut, `apply_approved_exceptions` vaut `false`, le seuil vaut `null` et
le périmètre vaut `global`. Les anciens profils conservent donc leurs décisions
informatives. Publier une nouvelle version du profil pour changer la politique.
Une revue `safe` ou `to_fix` compte comme effectuée ; elle n'est pas une
déclaration d'absence de vulnérabilité. Seuls les états `false_positive` et
`accepted_risk`, approuvés par un administrateur avec motif et expiration, peuvent
exclure une occurrence lorsque la politique l'autorise.

Lors de la création d'une publication, le serveur capture les décisions de son
projet et de sa branche sous le verrou de collaboration. Le snapshot contient
identité, décision, version, preuve, état, motif et expiration. Les traitements et
reprises utilisent ce snapshot et la date de création de la publication. Une
revue ultérieure ne réécrit ni le snapshot ni un verdict historique ; elle sera
prise en compte par une prochaine publication. Une publication rejouée avec le
même manifeste retrouve son reçu et son snapshot.

La décision de revue conserve elle-même `review_evidence` avec son commit et son
empreinte de preuve. Un scan ne peut pas rattacher rétroactivement cette décision
à sa propre preuve : seuls les fingerprints uniques ou une chaîne complète de
correspondances Git confirmées permettent sa continuité. Une ligne remplacée
sans fingerprint réinitialise la revue, même si le scanner répète le même
message. Une décision ancienne sans preuve figée reste informative jusqu'à une
nouvelle revue.

Une preuve modifiée, une empreinte ambiguë, une décision expirée ou une autre
branche ne peut pas appliquer la dérogation. Sans preuve conservée correspondant
à la dernière observation du finding, le serveur ne réutilise pas sa revue.
Les exceptions de profil continuent à suivre leur propre snapshot explicite.
Sans fingerprint, le même chemin et la même ligne ne suffisent pas entre deux
commits : il faut le même commit, ou un diff rattaché au commit de la décision
prouvant la ligne inchangée. Une ligne remplacée n'hérite pas de la dérogation.
Une correspondance devenue ambiguë supplante un ancien alias réussi : celui-ci
ne peut pas être réutilisé en ignorant l'indétermination plus récente.

## Taux et file de revue

`gate_snapshot.security_review` expose `numerator`, `denominator`, `value`,
`scope`, `threshold`, `state`, `result`, `captured_at` et les décisions utilisées.
Le dénominateur compte les occurrences de catégorie `security_hotspot` dans le
périmètre mesuré ; le numérateur compte celles ayant une revue valide pour la
même preuve. Les exceptions ne retirent pas silencieusement ces hotspots du
dénominateur. Le périmètre `new_code` utilise les états de comparaison complets,
jamais la liste de détails limitée à 100 findings.

Une absence de scanner ou de comparaison nécessaire donne `unknown`. Aucun
hotspot dans un périmètre effectivement mesuré donne `not_applicable`, une
valeur `null` et un dénominateur nul ; aucun pourcentage de 100 % n'est inventé.

Issues fournit le filtre `security_hotspot` dans le même parcours paginé que les autres catégories. Le détail expose outil, règle, message, localisation et priorité du scanner ; ses contrôles enregistrent la décision, et seul un administrateur peut accorder ou prolonger une exception.

Le bloc `Calculation evidence` d'une analyse présente le taux, les fractions, le seuil et les décisions figées. Une réapparition après résolution, une expiration
ou un changement de preuve réinitialise la revue pour éviter qu'une ancienne
décision `safe` soit réutilisée automatiquement.

## Vérification

`SecurityReviewTests` couvre l'approbation, l'expiration, la séparation des
branches, la réapparition, le changement de preuve, les données manquantes,
le cas non applicable, les déplacements uniques et ambigus. Avec PostgreSQL de
test isolé, il vérifie aussi l'assignation conservée, l'historique durable,
la réutilisation d'une correspondance et l'absence de transfert interbranches.
`CodeScopeTests` exerce une publication avec approbation et une autre après
révocation : seule la seconde perd l'exception, le premier verdict reste intact.
Les tests s'exécutent via `dotnet test tests/CodeInsights.Tests -c Release`
([exploitation](exploitation.md#développement-local)) ; ils ne constituent pas une
validation de déploiement Azure.

La purge d'ingestion efface les métadonnées détaillées `CodeData` et l'inventaire
`repository_files` ; les snapshots de verdict et décisions restent consultables.
