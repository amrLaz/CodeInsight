# CodeInsights

Lire dans l'éditeur le dernier rapport CodeInsights de la branche courante, et
estimer l'effet des changements en cours avant le push. L'extension consomme les
rapports produits par la CI ; elle ne reproduit pas le moteur du hub, et ne
conclut jamais à l'absence de problème à partir d'une analyse partielle.

L'architecture cible et les lots L0 à L9 sont décrits dans
[`docs/vscode-extension.md`](https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.CodeInsights?path=/docs/vscode-extension.md),
à la racine du dépôt.

## Installation

Le VSIX est produit par `npm run package` et par
[`.azuredevops/build-vscode-extension.yml`](https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.CodeInsights?path=/.azuredevops/build-vscode-extension.yml).
L'artefact de pipeline `CodeInsightsVsix` est la distribution interne ; le flux
Azure Artifacts `CodeInsights` reçoit le paquet `upcoop.codeinsights` lorsque la
variable `CODEINSIGHTS_VSIX_FEED` est définie sur le pipeline.

Dans VS Code : **Extensions: Install from VSIX…**, puis sélectionner le fichier
`codeinsights-<version>.vsix`.

## Configuration

Le walkthrough **CodeInsights** (`Aide → Bien démarrer`) reprend ces étapes dans
l'éditeur. Tant que l'URL du hub est vide, l'extension reste inactive.

### 1. Renseigner l'URL du hub

Paramètre `codeinsights.serverUrl`, par exemple `https://codeinsights.example`.
Il vaut pour la fenêtre entière ; c'est la clé de projet, elle, qui se résout
dossier par dossier.

### 2. Créer un jeton personnel

Dans le hub : avatar de l'appbar → **Personal access tokens**. Un jeton de
lecture suffit pour consulter ; ajouter la portée de triage pour traiter les
issues depuis l'éditeur. L'extension ne crée jamais de jeton elle-même.

### 3. Se connecter

Commande **CodeInsights : se connecter**, puis coller le jeton. Il n'est stocké
que dans `SecretStorage` — jamais dans les paramètres, jamais dans les journaux.
**CodeInsights : se déconnecter** l'efface.

### 4. Vérifier la résolution du projet

L'ordre de résolution est explicite, et une ambiguïté est signalée plutôt que
tranchée au hasard : afficher le gate d'un autre projet serait pire que de n'en
afficher aucun.

1. le paramètre `codeinsights.projectKey` du dossier ;
2. le fichier `.codeinsights/vscode.json` du dépôt, utile quand plusieurs
   projets cohabitent :

   ```json
   {
     "projectKey": "up-france-api",
     "projects": {
       "Up.France.Api": "up-france-api",
       "https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.Web": "up-france-web"
     }
   }
   ```

   `projects` lie un dossier ou une URL de dépôt — les formes SSH, HTTPS et
   `.git` sont considérées équivalentes — et `projectKey` lie le dépôt entier
   quand aucun dossier ne correspond ;
3. à défaut, la recherche par URL de clone auprès du hub.

Ce fichier est versionné avec le dépôt, donc partagé : c'est la seule source de
liaison que le serveur MCP embarqué lit aussi, et le seul moyen que l'agent et
l'IDE nomment à coup sûr le même projet. Le paramètre `codeinsights.projectKey`,
lui, reste local au poste.

Il porte aussi, pour le même dossier, les deux réglages d'analyse qui doivent
suivre la CI plutôt que le poste :

```json
{
  "projectKey": "up-france-api",
  "targetBranch": "origin/develop",
  "semgrepConfig": [".codeinsights/semgrep-mqr-v1.json"]
}
```

Contrairement à `projectKey`, ces deux-là **passent devant** `codeinsights.liveAnalysis.targetBranch`
et `codeinsights.liveAnalysis.semgrepConfig` : une branche de référence ou un jeu
de règles choisis poste par poste produiraient une estimation de gate que la
pipeline dément, sans rien à l'écran pour l'annoncer. Un champ absent laisse le
réglage du poste décider ; `"semgrepConfig": []` est une déclaration du dépôt, et
semgrep sera alors annoncé absent plutôt qu'exécuté avec des règles inconnues.

### 5. (Optionnel) Activer l'analyse locale

`CodeInsights : analyser mes changements` s'appuie sur `semgrep` et `gitleaks`.
Les binaires présents dans le `PATH` sont préférés ; sinon, activer
`codeinsights.liveAnalysis.useDocker` pour utiliser les images figées de
`codeinsights.liveAnalysis.images`, exécutées sans réseau. Un producteur
introuvable est déclaré absent, il n'est pas silencieusement ignoré.

Les règles viennent de `codeinsights.liveAnalysis.semgrepConfig`, par défaut
`.codeinsights/semgrep-mqr-v1.json` — le même jeu que la CI. Liste vide :
semgrep est déclaré absent plutôt qu'exécuté avec des règles inconnues.

`codeinsights.liveAnalysis.enabled` relance cette analyse après chaque
sauvegarde ; sans elle, l'analyse reste à la demande.

## Fonctionnalités

### Rapport de la branche

Le panneau **CodeInsights** (en bas, à côté de Problèmes et Terminal) affiche le
verdict de la dernière analyse, ses mesures et les conditions du gate ; la barre
d'état en reprend le verdict. Le rapport est rafraîchi toutes les
`codeinsights.refreshIntervalSeconds` secondes, et une nouvelle analyse de la
branche est signalée une fois. Un cache daté prend le relais quand le hub est
injoignable, la date restant visible. Une branche sans analyse reste vide :
aucun gate vert n'est inventé.

### Issues

La vue **Issues** groupe les findings par fichier, la pire sévérité en tête,
avec des filtres appliqués côté serveur : sévérité, catégorie, état et « les
miennes ». Chaque issue
s'ouvre à la ligne analysée, dans le hub, ou sur la définition de la règle
déclarée par le producteur. Les localisations secondaires et les flux de code
fournis par le producteur sont navigables dans l'arbre.

### Panneau Problèmes

Les issues sont publiées comme diagnostics, avec leur position recalée sur le
working tree en rejouant le diff depuis le commit analysé :

| Situation | Rendu |
| --- | --- |
| Ligne inchangée | replacée exactement |
| Ligne réécrite | « position approximative » |
| Commit indisponible localement | « position non vérifiée » |

Le soulignement couvre l'expression quand les colonnes sont connues et la
position fiable ; les localisations secondaires deviennent des
`relatedInformation`. Le survol détaille la règle et sa définition, le CodeLens
compte les issues du fichier. `codeinsights.diagnostics.enabled` désactive cette
publication.

### Traitement depuis l'éditeur

Assignation, changement d'état, commentaire et demande d'exception se font sans
quitter le code. Les états réservés aux administrateurs sont masqués pour les
autres comptes.

### Assistant

**Expliquer l'issue avec l'assistant** utilise le prompt du hub, construit sur la
source réellement analysée ; à défaut, un prompt bâti depuis le fichier local est
utilisé et annoncé comme tel. **Copier le prompt de l'issue** le place dans le
presse-papiers pour un autre outil. Le participant de chat `@codeinsights` n'est
enregistré que si l'hôte expose l'API de chat.

### Analyse des changements en cours

**CodeInsights : analyser mes changements** exécute les producteurs locaux sur
les fichiers du diff face à `codeinsights.liveAnalysis.targetBranch`, ne retient
que les issues **sur les lignes ajoutées**, applique les exceptions et les règles
désactivées du projet, puis publie le résultat dans une collection de
diagnostics distincte, `CodeInsights (local)`.

L'estimation du gate qui accompagne ce run n'est pas un verdict : les libellés
sont « aggravation probable », « amélioration possible », « aucune aggravation
détectée » ou « non conclusive ». Aucun « pass » local n'est affiché, et la barre
d'état conserve le verdict de la CI.

### Signaux dans l'éditeur

- **Couverture** : la gouttière reprend la couverture de la dernière analyse. Une
  couverture non mesurée n'est pas peinte. Un LCOV local déclaré dans
  `codeinsights.coverage.localLcov` la remplace s'il est plus récent.
- **Duplication** : les blocs dupliqués de la dernière analyse sont surlignés
  dans le fichier ouvert.
- **Dépendances** : `package.json`, `*.csproj` et `pubspec.yaml` sont annotés
  avec les CVE du SBOM de l'analyse. Une liste vide signifie « aucune CVE
  reçue », pas « sûr ».

### Catalogue de règles

La vue **Règles** liste les règles du projet, filtrables, avec ouverture dans le
hub. Le panneau de détail n'affiche aucune description de règle : le hub n'en
stocke pas, et l'extension n'en invente pas.

### Crochet Git pre-push

**CodeInsights : proposer le crochet Git pre-push** installe, sur demande
explicite uniquement, un rappel avant push. Il n'empêche jamais le push : une
estimation locale n'est pas le gate CI.

### Serveur MCP pour les agents

L'extension embarque le **serveur MCP CodeInsights** et le déclare à l'éditeur dès que
`codeinsights.serverUrl` est renseignée : les agents de l'éditeur — Copilot en mode agent,
notamment — y trouvent sept outils en lecture seule, sans qu'aucun fichier de configuration
ne soit écrit ni aucun secret recopié. Le jeton du `SecretStorage` est transmis au démarrage
du serveur ; sans connexion, le serveur n'est pas démarré et l'extension propose de se
connecter.

| Outil | Rend |
| --- | --- |
| `get_branch_report` | verdict, complétude, état technique, mesures, dérive du working tree |
| `list_findings` | issues triées par criticité, bornées, troncature annoncée |
| `explain_finding` | message, règle, localisations secondaires, contexte source |
| `describe_rule` | identité et statut de la règle dans le profil du projet |
| `get_quality_gate` | verdict et conditions non tenues |
| `get_file_quality` | couverture et duplication d'un fichier |
| `analyze_pending_changes` | findings sur les lignes ajoutées et estimation du gate |

L'analyse locale de l'agent utilise les réglages `codeinsights.liveAnalysis.*` du workspace.
Un changement d'URL, de réglage ou de jeton relance le serveur. Détails et voie npm dans
[`docs/mcp-server.md`](https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.CodeInsights?path=/docs/mcp-server.md).

### Workspace multi-root

Le dossier de l'éditeur actif détermine le projet suivi et les paramètres de
portée dossier. Le hub, lui, reste commun à la fenêtre.

## Paramètres

| Paramètre | Défaut | Rôle |
| --- | --- | --- |
| `codeinsights.serverUrl` | `""` | URL du hub. Vide : extension inactive. |
| `codeinsights.projectKey` | `""` | Clé de projet forcée pour le dossier. |
| `codeinsights.refreshIntervalSeconds` | `300` | Rafraîchissement du rapport ; `0` le désactive. |
| `codeinsights.diagnostics.enabled` | `true` | Publication des issues dans Problèmes. |
| `codeinsights.liveAnalysis.enabled` | `false` | Analyse locale après chaque sauvegarde. |
| `codeinsights.liveAnalysis.targetBranch` | `origin/master` | Base du diff ; repli sur `HEAD`. |
| `codeinsights.liveAnalysis.semgrepConfig` | `[".codeinsights/semgrep-mqr-v1.json"]` | Règles semgrep. |
| `codeinsights.liveAnalysis.useDocker` | `false` | Exécuter via Docker les producteurs absents du `PATH`. |
| `codeinsights.liveAnalysis.images` | semgrep `1.145.0`, gitleaks `v8.30.0` | Images figées, comme en CI. |
| `codeinsights.liveAnalysis.timeoutSeconds` | `120` | Délai maximal par producteur. |
| `codeinsights.liveAnalysis.maxFiles` | `100` | Fichiers modifiés soumis aux producteurs. |
| `codeinsights.coverage.enabled` | `true` | Couverture en gouttière. |
| `codeinsights.coverage.localLcov` | `""` | LCOV local, relatif au dépôt. |
| `codeinsights.duplication.enabled` | `true` | Surlignage des blocs dupliqués. |
| `codeinsights.dependencies.enabled` | `true` | Annotation CVE des manifestes. |

## Commandes

Toutes sont dans la palette sous la catégorie **CodeInsights**.

| Commande | Effet |
| --- | --- |
| Se connecter / Se déconnecter | Enregistre ou efface le jeton dans `SecretStorage`. |
| Rafraîchir le rapport de la branche | Relit la dernière analyse. |
| Ouvrir dans le hub | Ouvre la vue correspondante du hub. |
| Analyser mes changements | Producteurs locaux sur les lignes ajoutées. |
| Filtrer les issues / Effacer les filtres | Filtres serveur de la vue Issues. |
| Ouvrir l'issue dans le code / dans le hub | Navigation depuis la liste. |
| Ouvrir la définition de la règle | Lien déclaré par le producteur. |
| Traiter l'issue | Assignation, état, commentaire, exception. |
| Copier le prompt / Expliquer avec l'assistant | Contexte IA de l'issue. |
| Ouvrir la localisation / l'issue locale | Localisations secondaires et flux de code. |
| Filtrer les règles / Afficher la règle / Ouvrir dans le hub | Catalogue de règles. |
| Proposer le crochet Git pre-push | Installe le rappel, sur demande. |

## Développement

```bash
npm ci                 # dépendances
npm run check          # tsc --noEmit puis tests Vitest TypeScript
npm run build          # bundles dist/extension.js et dist/mcp-server.mjs (esbuild)
npm run watch          # bundle en continu pour le débogage F5
npm run package        # VSIX dans dist/
```

Ouvrir `apps/vscode` dans VS Code, lancer `npm run watch`, puis F5.

La couche métier — client d'API, transformations de rapport, analyse locale,
résolution de projet — vit dans `apps/core`, partagée avec le serveur MCP d'`apps/mcp`,
dont le bundle est embarqué dans le VSIX : lancer `npm ci` dans les deux avant de
construire l'extension, `npm run check` pour les valider, et `npm run api -- --url https://hub.example/openapi.json`
pour régénérer `src/api/schema.d.ts`, généré et non versionné. Le cœur n'importe
jamais `vscode` : ce qui dépend de l'éditeur reste ici, dans `extension.ts`,
`auth/`, `context/` et `views/`.
