---
title: Serveur MCP CodeInsights
description: Architecture cible, surface d'outils et plan de développement du serveur Model Context Protocol donnant aux agents IA un accès interrogeable au hub et à l'analyse locale.
---

# Serveur MCP CodeInsights

## Objet et statut

**Les lots A, B, C et E sont implémentés** : le cœur partagé
[`apps/core/`](../apps/core), le serveur et ses sept outils
[`apps/mcp/`](../apps/mcp), le bundle embarqué dans le VSIX et le pipeline de
publication npm. **Aucun pilote ne l'a encore exercé** : le flux npm `@upcoop`
n'existe pas et la publication attend la variable `CODEINSIGHTS_NPM_FEED` ; la
vérification sur le hub réel reste à faire (lot F).

Les deux paquets se valident depuis leur répertoire :

```powershell
cd apps/core ; npm ci ; npm run check
cd apps/mcp  ; npm ci ; npm run check
```

`apps/mcp/npm run check` compile, exécute les tests Vitest de configuration et de
rendu, construit le bundle, puis **parle réellement MCP au bundle distribué**
(`src/protocol.test.ts`) : la liste des outils, leurs annotations de lecture
seule et l'obligation de `cwd` sont vérifiées sur le serveur tel qu'il tournera.

Le serveur expose CodeInsights à un **agent IA** via le Model Context Protocol :
un agent y interroge le dernier rapport de branche, la liste des findings, la
définition d'une règle, le quality gate, et fait analyser ses changements en
cours avant de proposer un correctif. Il complète l'[extension VS
Code](vscode-extension.md) et l'[extension Visual Studio](visualstudio-extension.md) sans les remplacer.

Cette page complète le [contrat d'intégration](integration.md), qui reste la
référence des routes réellement disponibles, et la [matrice des
autorisations](security-access.md).

## Pourquoi une surface distincte de l'extension

L'extension et le serveur MCP servent deux consommateurs différents :

| | Extension VS Code | Serveur MCP |
| --- | --- | --- |
| Consommateur | une personne dans un éditeur | un agent sans interface |
| Interaction | *push* — diagnostics, gouttière, CodeLens, panneau | *pull* — l'agent interroge quand il en a besoin |
| Portée | VS Code et ses forks, avec dégradation | Copilot agent mode, Claude Code, Cursor, agents CI |

L'extension embarque déjà un participant de chat `codeinsights.assistant`
([participant.ts](../apps/vscode/src/chat/participant.ts)). Il est
**unidirectionnel et à un coup** : il pousse dans le modèle le prompt de
`GET /api/v1/issues/{occurrence_id}/prompt` et s'arrête. L'agent ne peut pas
demander l'état du gate, relire les findings après son correctif, ni consulter
la définition d'une règle. C'est cette boucle de rétroaction que MCP apporte, et
elle seule fait passer CodeInsights d'un rapport consulté à un outil de
correction.

Second écart, structurel : le participant repose sur `vscode.chat`, API que
`participant.ts` contourne déjà par une garde d'existence. MCP est un protocole
ouvert ; le même serveur sert l'IDE, la ligne de commande et la CI.

## Points d'appui existants

| Besoin | Ressource actuelle |
| --- | --- |
| Transport et pagination | [client.ts](../apps/core/src/api/client.ts) : Bearer, JSON `snake_case`, `application/problem+json`, `X-Next-Cursor` — sans dépendance à `vscode` |
| Transformations métier | `apps/core/src/report/` (15 modules : issues, couverture, duplication, règles, dérive, triage) — sans dépendance à `vscode` |
| Analyse locale | `apps/core/src/local/` : diff des lignes ajoutées, `semgrep`/`gitleaks` en binaire ou image figée, estimation du gate |
| Résolution dépôt → projet | `context/project-resolution.ts` et `context/project-key.ts`, sur `GET /api/v1/projects?repo_url=` |
| Identité poste de travail | `POST /api/v1/auth/me/tokens` — jeton personnel aux droits exacts de son propriétaire, expiration obligatoire, empreinte SHA-256, révocation idempotente |
| Scopes | `analysis:read` et `collaboration:write` ([AuthService.cs](../apps/api/Auth/AuthService.cs)) |
| Verdict et conditions | routes de [GateEndpoints](../apps/api/Endpoints/GateEndpoints.cs) |
| Couverture et duplication | [CodeQualityInsightEndpoints](../apps/api/Endpoints/CodeQualityInsightEndpoints.cs) |
| Contexte pour l'IA | [IssuePromptEndpoints](../apps/api/Endpoints/IssuePromptEndpoints.cs) |
| Tests du cœur | Fichiers `src/*.test.ts` exécutés par Vitest dans `apps/core` |

Le relevé qui a fondé ce découpage s'est vérifié au déplacement : `api/` et
`report/` n'importaient pas `vscode`, et le couplage résiduel se limitait aux
deux `vscode.workspace.fs.stat` de `local/session.ts`, à `gitApi()` dans
`local/session.ts` et `context/git-diff.ts`, et à trois fichiers de `context/`.
L'essentiel de ce dont le serveur MCP a besoin était donc déjà écrit et déjà
testé ; le lot A n'a rien réécrit.

**Aucune évolution de l'API du hub n'est nécessaire.** La surface d'outils se
construit entièrement sur les routes existantes.

## Architecture retenue

Un **serveur stdio Node unique**, construit une fois, distribué deux fois.

```text
apps/core/   cœur partagé sans dépendance à vscode (api, report, local, context, hub)
apps/vscode/ adaptateur VS Code : vues, diagnostics, gouttière, participant de chat
apps/mcp/    adaptateur MCP : outils, bornes de sortie, adaptateur Git/FS Node
```

`apps/core/` porte les 3 modules `api/`, les 15 modules `report/`, les 7 modules
`local/`, `hub/deep-link.ts` et la partie pure de `context/` — `paths.ts`,
`project-key.ts`, `project-resolution.ts` et `git-diff.ts`. Les tests
`*.test.ts` utilisent Vitest dans `apps/core` ; `apps/vscode` conserve
`severity-colors.test.ts`, qui vérifie que la couleur nommée par
le cœur est bien contribuée par son manifeste. Restent dans l'extension les trois
fichiers `context/` qui dépendent de l'éditeur — `git-context.ts`,
`workspace-file.ts`, `workspace-folder.ts` — et l'adaptateur `context/workspace.ts`.

`apps/core/` est la garantie qu'il n'existe qu'une implémentation de la
résolution de projet, du recalage des positions sur le working tree et de
l'estimation du gate. Deux implémentations divergeraient, et l'IDE et l'agent
donneraient alors deux verdicts différents sur le même dépôt.

Les deux applications étant bundlées par esbuild, `apps/mcp` et `apps/vscode`
importent `apps/core` par chemin relatif : ni workspace npm, ni paquet
intermédiaire à publier. Chaque répertoire reste un projet npm autonome avec son
propre `npm run check`, comme `apps/web` et `apps/vscode` aujourd'hui.

Le port introduit est
[`context/ports.ts`](../apps/core/src/context/ports.ts) :
`{ repositories(rootPath), exists(path) }`. L'extension le sert avec l'extension
Git et `vscode.workspace.fs`
([`context/workspace.ts`](../apps/vscode/src/context/workspace.ts)), le serveur MCP
avec le binaire `git` et `node:fs`
([`src/workspace.ts`](../apps/mcp/src/workspace.ts)). Le port couvre le diff parce
que `local/session.ts` et `context/git-diff.ts` en ont besoin : l'IDE doit suivre
le dépôt sélectionné dans sa vue SCM, là où un agent n'a pas d'éditeur.

### Transport

stdio uniquement. Un transport HTTP servi par le hub serait la bonne cible pour
les agents qui ne tournent pas sur le poste — bots de revue, agents CI — mais il
demande le SDK MCP .NET dans `apps/api` et ne peut pas porter l'analyse locale,
qui exige l'accès au working tree. Il est hors périmètre de cette page.

## Surface d'outils

Les outils sont conçus sur les **intentions** de l'agent, pas sur les routes de
l'API. Un outil par route produirait une quarantaine d'outils génériques dans
lesquels l'agent se perd, pour une charge de maintenance sans contrepartie.

| Outil | Entrées | Rend |
| --- | --- | --- |
| `get_branch_report` | `cwd`, `branch?` | verdict, complétude, état technique, mesures et dérive du working tree ; une branche sans build de branche rend le dernier build de PR issu d'elle, nommé `PR #N · source → cible` |
| `list_findings` | `cwd`, `branch?`, `paths?`, `severity?`, `limit?` | findings triés par sévérité, bornés, troncature annoncée |
| `explain_finding` | `cwd`, `finding_id`, `include_source?` | message, règle, localisations secondaires et contexte source |
| `describe_rule` | `cwd`, `rule_id` | définition de la règle dans le profil du projet, pas la violation |
| `get_quality_gate` | `cwd`, `branch?` | verdict et conditions non tenues |
| `get_file_quality` | `cwd`, `path` | couverture et duplication du fichier |
| `analyze_pending_changes` | `cwd`, `target_branch?` | findings sur les lignes ajoutées et estimation du gate |

Deux écarts avec la surface d'abord esquissée, décidés en implémentant :

- **`new_code_only` n'existe pas.** Le hub n'attribue pas le New Code au finding :
  c'est une portée de condition de gate, adossée à une baseline de commit, et
  `GET /api/v1/insights/analyses/{id}/findings` ne l'expose sur aucun filtre
  ([InsightEndpoints](../apps/api/Endpoints/InsightEndpoints.cs) filtre `category`,
  `severity`, `rule`, `path`, `state`, `mine`, `team_id`). Un paramètre que le
  serveur aurait accepté sans pouvoir l'honorer aurait rendu la liste complète en
  la faisant passer pour la liste du code neuf. La question « qu'est-ce que mon
  changement introduit ? » est celle d'`analyze_pending_changes`, qui la mesure
  réellement, sur les lignes ajoutées.
- **`describe_rule` prend `cwd`.** Le statut d'une règle — activée, désactivée,
  non gérée — appartient au profil du projet, pas à la règle. Sans `cwd`, l'outil
  aurait rendu une identité sans le seul renseignement qui décide d'agir ou non.

### Règles de conception

- **Résolution implicite du projet.** L'agent fournit un `cwd`, jamais une
  `project_key`. Le serveur lit d'abord la configuration que le dépôt porte sur
  lui-même — `.codeinsights/vscode.json`, cherché depuis `cwd` puis en remontant
  jusqu'à la racine du clone — puis retombe sur la chaîne `git remote` →
  `repo_url` → projet. C'est le fichier que lit déjà l'extension VS Code : sans
  lui, l'agent et l'IDE pourraient nommer deux projets différents pour un même
  dossier de monorepo, sans rien à l'écran pour le dire. Exiger une clé de
  projet, à l'inverse, ferait inventer des valeurs à l'agent. Un fichier présent
  mais illisible est signalé, jamais contourné en silence.
- **Les réglages d'analyse viennent du dépôt.** `targetBranch` et `semgrepConfig` de
  `.codeinsights/vscode.json` l'emportent sur `CODEINSIGHTS_TARGET_BRANCH` et
  `CODEINSIGHTS_SEMGREP_CONFIG`. L'environnement d'un serveur déclaré en portée
  utilisateur est écrit une fois, pour le projet ouvert ce jour-là, puis suit l'agent
  dans tous les autres dépôts : il ne peut être qu'un repli. `resolveTarget` rend ces
  réglages effectifs dans `Target.local`, et `analyze_pending_changes` n'en lit pas
  d'autres. `input.target_branch` continue de primer sur les deux.
- **Sortie bornée.** `limit` par défaut à 30, tri par sévérité, rendu texte
  compact plutôt que JSON brut. Une liste de findings non bornée sature la
  fenêtre de contexte de l'agent et dégrade toutes ses réponses suivantes.
- **Erreurs exploitables.** Un 401 doit dire que le jeton a expiré et comment en
  créer un, pas « HTTP 401 ». Le `detail` du `problem+json` est déjà extrait par
  `problemDetail()`.
- **Absence déclarée, jamais silencieuse.** Un producteur local manquant, une
  mesure absente ou une branche sans analyse doivent être annoncés comme tels. Un
  build de pull request porte son nom (`PR #N · source → cible`) dans chaque réponse :
  un agent ne doit pas le lire comme le verdict de la branche.
  Un agent qui lit « 0 issue » là où l'outil n'a pas tourné conclura faux et
  livrera — c'est le mode d'échec le plus coûteux de cette intégration.
- **Estimation annoncée.** L'estimation de gate porte son caractère estimé dans
  le texte retourné, comme dans l'extension.

### Lecture seule

La première livraison **n'expose aucun outil d'écriture**. La garantie vient de
la surface d'outils, pas d'une vérification de jeton : il n'existe pas d'outil à
appeler.

Le serveur **n'exige donc pas** un jeton restreint à `analysis:read` et ne
refuse pas un jeton portant `collaboration:write`. L'extension pratique déjà le
triage depuis son lot L5, donc le jeton présent dans son `SecretStorage` porte
souvent ce scope ; le refuser casserait la distribution embarquée pour les
utilisateurs les plus avancés. Un scope excédentaire est ignoré, avec une ligne
de journal.

Le triage depuis un agent est décrit plus bas comme une évolution différée.

## Secret et scopes

| Voie de distribution | Origine du jeton |
| --- | --- |
| Embarquée dans le VSIX | `SecretStorage` de l'extension, injecté par `resolveMcpServerDefinition` au démarrage du serveur |
| Embarquée, déclarée à Claude Code | `SecretStorage`, écrit dans la configuration personnelle par `codeinsights.configureMcpClient` — ou marqueur `${CODEINSIGHTS_TOKEN}` en portée dépôt |
| Paquet npm | variable d'environnement `CODEINSIGHTS_TOKEN` |

La voie embarquée réutilise le jeton de l'extension tel quel, quel que soit son
scope : l'utilisateur n'a rien à créer ni à coller.

Pour la voie npm, créer un **jeton dédié au scope `analysis:read` seul**. Il vit
dans une variable d'environnement, donc plus exposé qu'un `SecretStorage` ;
`analysis:read` est déjà la valeur par défaut de `POST /api/v1/auth/me/tokens`.
Le jeton ne doit jamais figurer dans un `.mcp.json` versionné :

```jsonc
{
  "mcpServers": {
    "codeinsights": {
      "command": "npx",
      "args": ["-y", "@upcoop/codeinsights-mcp"],
      "env": {
        "CODEINSIGHTS_URL": "https://codeinsights.example",
        "CODEINSIGHTS_TOKEN": "${CODEINSIGHTS_TOKEN}"
      }
    }
  }
}
```

Tant que le paquet n'est pas publié, le bundle se lance depuis le dépôt :
`"command": "node", "args": ["<dépôt>/apps/mcp/dist/server.js"]`, après
`npm ci && npm run build` dans `apps/mcp`.

Toute la configuration passe par l'environnement, et aucune valeur n'est devinée :
sans `CODEINSIGHTS_URL` ni `CODEINSIGHTS_TOKEN`, chaque outil répond ce qui manque
et comment le fournir, plutôt que d'interroger un hub au hasard.

| Variable | Rôle | Défaut |
| --- | --- | --- |
| `CODEINSIGHTS_URL` | URL du hub | aucune — obligatoire |
| `CODEINSIGHTS_TOKEN` | jeton personnel | aucun — obligatoire |
| `CODEINSIGHTS_LIMIT` | findings rendus par défaut | `30`, plafonné à `200` |
| `CODEINSIGHTS_TARGET_BRANCH` | branche de comparaison de l'analyse locale | `origin/master` |
| `CODEINSIGHTS_SEMGREP_CONFIG` | règles semgrep, séparées par virgule ou espace | vide — semgrep est alors annoncé absent |
| `CODEINSIGHTS_USE_DOCKER` | exécuter les producteurs par image figée | `false` |
| `CODEINSIGHTS_SEMGREP_IMAGE`, `CODEINSIGHTS_GITLEAKS_IMAGE` | images figées | celles de l'extension |
| `CODEINSIGHTS_TIMEOUT_SECONDS` | délai par producteur | `120` |
| `CODEINSIGHTS_MAX_FILES` | fichiers analysés par exécution | `100` |

Les règles complètes de délivrance et de révocation restent dans
[security-access.md](security-access.md).

## Analyse locale

`analyze_pending_changes` est l'outil à plus forte valeur, et le seul qu'un
transport distant ne pourra jamais rendre : il lit le working tree.

Il réutilise `local/` sans réécriture — diff des lignes ajoutées, `semgrep` et
`gitleaks` en binaire du `PATH` ou en image Docker figée, exceptions et règles
désactivées du projet appliquées, puis estimation du gate. Les défauts viennent
des variables ci-dessus ; lancé par l'extension, le serveur reçoit dans ces mêmes
variables les valeurs `codeinsights.liveAnalysis.*` du workspace.

Le `timeoutSeconds` existant s'applique. Un dépassement retourne un **résultat
partiel annoté**, jamais une erreur nue : un agent doit pouvoir distinguer « rien
trouvé » de « pas fini ». La même règle couvre le reste du parcours : un
producteur absent, un plafond de fichiers atteint, une politique de projet que le
hub a refusé de rendre sont tous écrits dans la réponse. Une exécution sans
producteur rend « aucun producteur local disponible : rien n'a été mesuré » et une
estimation « non conclusive » — jamais un silence qui se lirait comme un feu vert.

Aucun code source ne quitte le poste : les producteurs locaux s'exécutent sans
réseau, et le serveur n'envoie au hub que des identifiants et des requêtes de
lecture.

## Distribution

Un **seul artefact de build** alimente les deux voies : `apps/mcp` produit un
bundle esbuild unique, sans dépendance runtime à résoudre. Les deux voies ne
peuvent donc pas diverger.

### Voie 1 — embarquée dans le VSIX

`apps/vscode/esbuild.mjs` construit `dist/mcp-server.mjs` avec les options
d'`apps/mcp` ([`esbuild.options.mjs`](../apps/mcp/esbuild.options.mjs)) : une seule
définition du bundle, deux sorties. `npm ci` dans `apps/mcp` est donc un prérequis
du build de l'extension. L'extension pèse 236 Ko empaquetée, dont 757 Ko de
serveur minifié.

L'extension enregistre le fournisseur `codeinsights.mcp`
([`src/mcp/provider.ts`](../apps/vscode/src/mcp/provider.ts),
`contributes.mcpServerDefinitionProviders`, API disponible depuis VS Code 1.101 ;
l'extension cible `^1.104.0`). Il est protégé par la même garde d'existence que le
participant de chat : un fork sans cette API journalise et se tait.

- `provideMcpServerDefinitions` rend le serveur dès que `codeinsights.serverUrl`
  est renseignée, avec les réglages `liveAnalysis.*` traduits en variables
  ([`src/mcp/environment.ts`](../apps/vscode/src/mcp/environment.ts)). Sans URL, ou
  sans bundle dans le paquet, il ne déclare rien : un serveur qui ne peut que
  répondre « configure-moi » encombre la liste MCP de l'utilisateur.
- `resolveMcpServerDefinition` y ajoute le jeton du `SecretStorage`. Le contrat de
  l'API interdit toute interaction dans `provide` ; c'est ici, et seulement ici,
  que la connexion peut être proposée. Sans jeton, le serveur n'est pas démarré.
- Le serveur est relancé après un changement d'URL, de réglage `liveAnalysis` ou
  de jeton (`onDidChangeMcpServerDefinitions`) : les outils héritent de
  l'environnement du démarrage.

La commande est `process.execPath` avec `ELECTRON_RUN_AS_NODE=1` — le Node de
l'éditeur, rien à installer sur le poste. Le fichier est `.mjs` parce que le paquet
de l'extension est CommonJS : un `.js` ESM y ferait reparser le bundle à chaque
démarrage, avec un avertissement dans le journal du serveur.

Le développeur n'écrit aucune configuration et ne manipule aucun secret. Le
serveur est lancé depuis le fichier embarqué, **jamais par `npx`** : le poste
peut être hors ligne, et une résolution réseau au démarrage d'un serveur MCP est
un mode d'échec silencieux.

Le contrat entre les deux paquets — noms des variables, identifiant du
fournisseur, nom du bundle — est rejoué des deux côtés par
`apps/vscode/src/mcp-definition.test.ts` : il construit l'environnement depuis
des réglages et le relit avec `readConfig` du serveur. Un renommage d'un seul côté
ne casse rien de visible — le serveur repartirait sur ses défauts et l'agent
analyserait avec d'autres règles que l'IDE.

### Voie 1 bis — le même bundle, déclaré à Claude Code

`registerMcpServerDefinitionProvider` alimente le registre MCP **de l'éditeur** —
Copilot agent mode — et lui seul. **Claude Code ne le lit pas**, même lancé dans le
terminal intégré de VS Code : il découvre ses serveurs dans sa propre configuration
(`.mcp.json` du dépôt, configuration personnelle, `claude mcp add`). Le fournisseur de
la voie 1 ne lui apporte donc rien. Le bundle, lui, le sert sans modification : il parle
stdio et lit toute sa configuration dans l'environnement.

Ce qui manquait n'était pas le serveur mais **la déclaration et le secret**. La commande
**« CodeInsights : configurer Claude Code »** (`codeinsights.configureMcpClient`,
[`src/mcp/claude-setup.ts`](../apps/vscode/src/mcp/claude-setup.ts)) les fournit, sans que
l'utilisateur écrive de configuration ni manipule de jeton — la propriété qui justifie la
voie embarquée.

| Portée | Fichier | Jeton | Mise à jour |
| --- | --- | --- | --- |
| Ce poste de travail | configuration personnelle de Claude Code, via `claude mcp add-json --scope user` | celui du `SecretStorage`, écrit en clair dans un fichier non versionné | automatique : connexion, déconnexion, changement d'URL ou de réglage `liveAnalysis` |
| Ce dépôt | `.mcp.json` à la racine du dépôt, versionné | jamais — seulement le marqueur `${CODEINSIGHTS_TOKEN}`, que Claude développe au démarrage | manuelle : le fichier appartient à l'équipe |

La portée dépôt ne porte pas le jeton parce qu'un jeton porte les droits de son
propriétaire, pas ceux du dépôt : chacun exporte `CODEINSIGHTS_TOKEN` dans son
environnement. Claude Code demande par ailleurs l'approbation d'un serveur déclaré par un
`.mcp.json` au premier démarrage — un fichier commité ne lance rien tout seul.

Quatre décisions méritent d'être connues avant de toucher ce code :

- **Le bundle est recopié hors du répertoire d'installation**, dans `globalStorageUri`.
  Le chemin du VSIX porte le numéro de version — `upcoop.codeinsights-0.7.0` — et disparaît
  à la mise à jour suivante. Sans recopie, la configuration de Claude désignerait un
  fichier supprimé, et l'agent lirait l'absence de serveur comme une absence de problème.
  L'activation réaligne la copie et la configuration quand la version a changé, et
  seulement dans ce cas.
- **Rien n'est écrit avant que le serveur ait répondu.** La commande lance le bundle tel
  qu'elle va le déclarer et lui parle réellement MCP — un `initialize`, quinze secondes de
  patience — avant d'écrire. Le jeton n'entre pas dans cette vérification.
- **La commande lancée est celle de la voie 1** : `process.execPath` avec
  `ELECTRON_RUN_AS_NODE=1`, le Node de l'éditeur. Claude Code s'installe sans Node sur le
  `PATH` ; `npx` supposerait un poste en ligne au démarrage du serveur.
- **La déconnexion retire l'entrée** (`claude mcp remove`). Un jeton révoqué qui resterait
  déclaré ferait échouer chaque outil sans que l'agent puisse en dire la raison.

Si `claude` n'est pas sur le `PATH` de l'hôte d'extension, la commande propose un terminal
portant la ligne à exécuter, sans l'exécuter : elle contient le jeton, et le faire entrer
dans l'historique du shell est une décision de l'utilisateur.

#### La proposition du démarrage

Une commande de palette que personne ne cherche n'est pas découverte. L'activation propose
donc la configuration — une notification, deux réponses : **Configurer** ou **Ne plus
proposer**. Fermer la notification sans répondre la reporte au démarrage suivant ; elle
n'est pas oubliée pour autant.

Cinq conditions doivent tenir, dans cet ordre, pour qu'elle s'affiche
([`claude-client.ts`](../apps/vscode/src/mcp/claude-client.ts), `setupNotice`) : le refus
n'a pas déjà été enregistré, `codeinsights.serverUrl` est renseignée, un jeton est présent,
**la commande `claude` répond sur ce poste**, et le serveur ne lui est pas déjà déclaré.
La présence de `claude` est demandée avant l'état de la déclaration parce qu'elle seule dit
si l'utilisateur se sert de Claude Code : proposer une configuration Claude à quelqu'un qui
ne l'a pas installé est du bruit, et une notification que l'on apprend à ignorer emporte
les suivantes.

Chaque condition qui bloque **écrit sa raison dans le journal** de l'extension. Une
notification qui ne s'affiche pas et ne s'explique pas est indébogable.

L'état de la déclaration est demandé à Claude lui-même — `claude mcp get codeinsights`, un
seul processus qui répond aussi à « `claude` est-il installé ? » — et non au `globalState`
de l'extension : l'utilisateur a pu retirer l'entrée à la main, et la proposition doit
alors revenir. Ce retrait délibéré efface aussi la portée enregistrée, sans quoi le
prochain changement de jeton redéclarerait un serveur qui venait d'être enlevé.

« Ne plus proposer » vaut pour le poste, comme la configuration personnelle qu'il refuse.
Il n'est pas définitif : lancer la commande depuis la palette lève le refus.

Les `instructions` déclarées par [`server.ts`](../apps/mcp/src/server.ts) sont reprises par
Claude Code dans son invite système — ce n'est pas vrai de tous les hôtes MCP. Les trois
lignes sur ce que la CI n'a pas publié arrivent donc là où elles doivent arriver.

Les outils apparaissent sous `mcp__codeinsights__*`, et Claude Code demande confirmation à
chaque appel tant qu'ils ne sont pas autorisés. L'annotation `readOnlyHint` n'y suffit pas ;
un dépôt qui veut la boucle sans friction l'écrit dans `.claude/settings.json` :

```jsonc
{ "permissions": { "allow": ["mcp__codeinsights"] } }
```

Deux limites assumées : le chemin du binaire de l'éditeur est figé à l'écriture — déplacer
l'application demande de rejouer la commande — et le `.mcp.json` du dépôt n'est jamais
réécrit tout seul, y compris après un changement d'URL du hub.

### Voie 2 — paquet npm interne

[build-mcp-server.yml](../.azuredevops/build-mcp-server.yml), calqué sur
[build-vscode-extension.yml](../.azuredevops/build-vscode-extension.yml), publie
`@upcoop/codeinsights-mcp` sur le flux npm Azure Artifacts, en plus de
l'artefact de pipeline. Elle sert Claude Code, Cursor et les agents CI.

La publication est conditionnée à la variable `CODEINSIGHTS_NPM_FEED` — l'URL de
registre du flux — comme le VSIX l'est à `CODEINSIGHTS_VSIX_FEED`. Le `.npmrc` est
écrit par le pipeline et n'est pas versionné : un registre codé dans le dépôt
ferait chercher les dépendances publiques sur le flux interne à chaque `npm ci`.
Il ne redirige que le scope `@upcoop`.

Le tarball ne contient que `dist/server.js` et le README : le SDK MCP et `zod`
sont des dépendances de développement, inlinées dans le bundle. `npx` n'a donc
rien à résoudre au-delà du paquet lui-même, et `prepack` reconstruit le bundle en
`--production` à chaque `npm pack` ou `npm publish` — un tarball ne peut pas
contenir le bundle de la veille.

Le flux npm interne exige un `.npmrc` authentifié sur chaque poste : c'est le
seul point de friction de cette voie, et la raison pour laquelle la voie 1
existe.

### Conséquences sur les pipelines

- `build-vscode-extension.yml` construit le bundle MCP **avant** `vsce package`
  (c'est `npm run package` qui produit les deux bundles), installe les dépendances
  d'`apps/mcp` et joue ses checks. Ses `paths.include` et son filtre de runtime
  couvrent `apps/core/**` et `apps/mcp/**`.
- La version publiée est **lue depuis `package.json`** dans les deux pipelines.
  `npm publish` refuse une version déjà publiée : une republication sans montée de
  version échoue bruyamment au lieu d'écraser un paquet déjà installé.
- **La montée de version est exigée à la PR**, pas à la publication. Le job `Scope` des
  deux pipelines compare la version du manifeste entre la base de fusion et `HEAD` dès
  lors que le paquet est touché — `apps/vscode/package.json` pour le VSIX,
  `apps/mcp/package.json` pour le tarball — et rejette la PR si elle n'a pas bougé ou si
  elle recule. Un changement d'`apps/core/` touche les deux paquets et exige donc les deux
  montées. Après la fusion, il ne resterait qu'un échec de publication sur `master` : la
  garde est placée au dernier endroit où elle bloque encore quelque chose. La règle est
  rappelée dans [AGENTS.md](../AGENTS.md).
- Le flux npm et le scope `@upcoop` doivent exister avant le premier
  `npm publish`, et `CODEINSIGHTS_NPM_FEED` être renseignée. Tant qu'elle ne l'est
  pas, le pipeline construit et vérifie le tarball sans le publier.

## Plan de développement

Une personne à temps partiel, lots livrables indépendamment. Les durées sont des
jours-homme de développement, hors revue et déploiement.

| Lot | Contenu | Dépendances | État |
| --- | --- | --- | --- |
| **A** | Cœur partagé `apps/core` : déplacement de `api/`, `report/`, `local/`, `context/`, port `ports.ts` sur `session.ts` et `git-diff.ts`, assertions déplacées | — | fait |
| **B** | Serveur MCP et six outils de lecture, bornes de sortie, adaptateur Git/FS Node, `protocol.test.ts` | A | fait |
| **C** | `analyze_pending_changes` : producteurs locaux, estimation du gate, résultat partiel annoté | A, B | fait |
| **E** | Distribution : bundle unique, `McpServerDefinitionProvider`, pipeline `build-mcp-server.yml`, flux npm | B, C | fait, sauf création du flux npm |
| **F** | Documentation, entrée dans `docs/index.md` et `AGENTS.md`, pilote | E | doc faite, pilote à faire — 1 j |

Le chemin critique restant est le pilote. Le lot D — triage en écriture — est
**volontairement reporté après le pilote** et décrit plus bas.

Le lot A a de la valeur indépendamment du reste : il clarifie la couche métier
de l'extension seule, en séparant son cœur testable de son adaptateur VS Code.

### Critères d'acceptation

- **Lot A** ✔ : les tests Vitest passent depuis `apps/core` ; `apps/vscode`
  compile et garde les tests qui lui appartiennent. Le cœur ne contient plus
  aucun `import ... from 'vscode'`.
- **Lot B** — vérifié hors ligne par `protocol.test.ts` et par un hub simulé
  (résolution `git remote` → `repo_url` → projet, rapport, findings, règle,
  couverture). **Reste à vérifier sur le hub réel** : sur ce dépôt et sur les trois
  pilotes, le verdict et le nombre d'issues rendus par `get_branch_report` doivent
  être identiques à ceux du hub pour la même analyse. Une branche sans analyse rend
  « aucune analyse », jamais un gate vert.
- **Lot C** — le chemin « aucun producteur installé » est vérifié : l'outil
  annonce l'absence et rend une estimation non conclusive. **Reste à vérifier avec
  `semgrep` installé** : un agent à qui l'on demande de corriger les problèmes de
  qualité de ses changements en cours appelle `analyze_pending_changes`, propose un
  correctif, rappelle l'outil et constate la disparition du finding — sans
  intervention humaine entre les deux appels, et sans avoir désactivé une règle.
- **Lot E** — le bundle embarqué a été piloté en stdio réel (`process.execPath`,
  `ELECTRON_RUN_AS_NODE=1`, le fichier tel qu'empaqueté) : initialisation, liste
  des sept outils et appel. **Reste à vérifier dans l'éditeur** : l'installation du
  VSIX suffit à faire apparaître les outils dans Copilot agent mode, sans qu'aucun
  fichier de configuration ne soit écrit par l'utilisateur.
- **Voie 1 bis** — la forme de l'entrée écrite par la commande a été acceptée par
  `claude mcp add-json`, qui rend le serveur « ✔ Connected » sur le bundle empaqueté.
  **Reste à vérifier sur le poste d'un pilote** : la commande écrite depuis l'éditeur,
  puis les sept outils appelés depuis Claude Code sans qu'aucun jeton ne soit collé.

## Évolution différée — triage en écriture

`triage_finding(finding_id, action, comment?)`, sur `report/triage.ts` et
`api/collaboration.ts`, scope `collaboration:write`, annoté `destructiveHint`
pour que les hôtes MCP demandent confirmation, désactivé par défaut derrière
`CODEINSIGHTS_MCP_WRITE=1`. La concurrence optimistique 409 déjà en place doit
remonter comme un message explicite invitant à relire la décision d'un collègue.
Charge estimée : 2 j.

**Critère d'entrée** : avoir observé, sur le pilote, comment les agents se
servent des outils de lecture — en particulier s'ils cherchent à faire
disparaître un finding par le triage plutôt que par un correctif. Ce risque est
la raison du report.

## Limites et non-objectifs

- Le serveur ne reproduit pas le moteur du hub. Il consomme des rapports
  produits par la CI et, pour le mode local, le sous-ensemble volontairement
  réduit de scanners déjà retenu par l'extension.
- Il n'apporte aucune capacité d'analyse nouvelle. Sa valeur est l'accès
  interrogeable, pas la détection.
- Il n'est utile qu'à proportion de ce que le hub contient. Un serveur MCP
  au-dessus d'un hub que la CI n'alimente pas n'a rien à répondre : le
  déploiement suit l'avancement du pilote Azure DevOps rappelé par le
  [README](../README.md) et l'[intégration des builds](integration.md).
- Le transport HTTP pour les agents hors poste n'est pas traité ici.
