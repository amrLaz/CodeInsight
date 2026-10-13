# Analyse locale

**Analyser mes changements** exécute `semgrep` et `gitleaks` sur les lignes ajoutées. Le résultat est une **estimation** : ce n'est pas le verdict de la CI. Aucun « pass » local n'est affiché. Un producteur absent est déclaré absent.

Le crochet Git pre-push est proposé par une commande dédiée ; il n'est jamais installé tout seul et n'empêche pas le push.
