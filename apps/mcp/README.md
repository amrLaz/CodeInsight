# @upcoop/codeinsights-mcp

Serveur [Model Context Protocol](https://modelcontextprotocol.io) donnant à un agent IA
un accès interrogeable à [CodeInsights](https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.CodeInsights) :
rapport de branche, issues, règles, quality gate, couverture et duplication, plus
l'analyse locale des changements en cours.

Transport **stdio**, **lecture seule** : aucun outil d'écriture n'existe dans cette
livraison. Aucun code source ne quitte le poste — les producteurs locaux s'exécutent sans
réseau et le hub n'est qu'interrogé.

## Configuration

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

Le jeton est un jeton personnel créé depuis le hub (Compte → jetons), scope
`analysis:read`. Il ne doit jamais être écrit dans un fichier versionné.

| Variable | Rôle | Défaut |
| --- | --- | --- |
| `CODEINSIGHTS_URL` | URL du hub | obligatoire |
| `CODEINSIGHTS_TOKEN` | jeton personnel | obligatoire |
| `CODEINSIGHTS_LIMIT` | findings rendus par défaut | `30` (max `200`) |
| `CODEINSIGHTS_TARGET_BRANCH` | branche de comparaison de l'analyse locale | `origin/master` |
| `CODEINSIGHTS_SEMGREP_CONFIG` | règles semgrep, séparées par virgule ou espace | vide |
| `CODEINSIGHTS_USE_DOCKER` | producteurs par image figée | `false` |
| `CODEINSIGHTS_SEMGREP_IMAGE`, `CODEINSIGHTS_GITLEAKS_IMAGE` | images figées | versions de l'extension |
| `CODEINSIGHTS_TIMEOUT_SECONDS` | délai par producteur | `120` |
| `CODEINSIGHTS_MAX_FILES` | fichiers analysés par exécution | `100` |

## Outils

| Outil | Rend |
| --- | --- |
| `get_branch_report` | verdict, complétude, état technique, mesures, dérive du working tree |
| `list_findings` | issues triées par criticité, bornées, troncature annoncée |
| `explain_finding` | message, règle, localisations secondaires, contexte source |
| `describe_rule` | identité et statut de la règle dans le profil du projet |
| `get_quality_gate` | verdict et conditions non tenues |
| `get_file_quality` | couverture et duplication d'un fichier |
| `analyze_pending_changes` | findings sur les lignes ajoutées et estimation du gate |

Chaque outil prend un `cwd` situé dans le dépôt, jamais une clé fournie par l'agent. Le
projet est résolu depuis `.codeinsights/vscode.json` — cherché depuis `cwd` puis en
remontant jusqu'à la racine du clone, `projects` avant `projectKey` — puis, à défaut,
depuis l'URL du dépôt Git. C'est le fichier que lit aussi l'extension VS Code : un
monorepo y nomme chacun de ses projets, et l'agent répond alors sur le même que l'IDE.

Le même fichier porte `targetBranch` et `semgrepConfig`, qui priment sur
`CODEINSIGHTS_TARGET_BRANCH` et `CODEINSIGHTS_SEMGREP_CONFIG` : l'environnement d'un
serveur déclaré une fois pour toutes sur un poste porte les réglages du dépôt qui était
ouvert ce jour-là, et ne peut être qu'un repli.

Ce que la CI n'a pas publié n'existe pas ici : une branche sans analyse le dit, une mesure
absente n'est jamais rendue comme zéro, et une analyse locale sans producteur installé
annonce qu'elle n'a rien mesuré.

## Développement

```powershell
npm ci
npm run check   # tsc, bundle, puis tests Vitest dont le dialogue MCP réel
npm run build   # dist/server.js
```

Le serveur s'appuie sur `apps/core`, le cœur partagé avec l'extension VS Code : y lancer
`npm ci` avant `npm run check`. Architecture et décisions dans
[`docs/mcp-server.md`](https://dev.azure.com/up-group/Up.France.ODI/_git/Up.France.CodeInsights?path=/docs/mcp-server.md).
