# AGENTS.md — application web

Portée : `apps/web/`. Les règles de [la racine](../../AGENTS.md) s'appliquent aussi ici. Pour toute modification d'interface, lire [DESIGN.md](../../DESIGN.md) avant d'éditer ; [docs/interface.md](../../docs/interface.md) précise les comportements et la validation. Ne pas changer l'identité visuelle ni les contrats API au prétexte d'une réorganisation.

## Repères

Arborescence à suivre :

```text
apps/web/
├── public/                 ressources servies telles quelles par Vite
├── src/
│   ├── components/
│   │   ├── ui/              Button, Checkbox, Grid, ListBox, TextArea, TextBox,
│   │   │                    ui.css, index.ts et tests colocalisés
│   │   └── common/          DialogCloseButton, InfoHint, ProjectPicker, ThemeToggle
│   │                        (CSS/tests du sélecteur ici)
│   ├── config/              env.ts, navigation.ts
│   ├── context/             ToastProvider.tsx
│   ├── features/
│   │   ├── projects/        Projects.tsx
│   │   │   └── components/   AnalysisEvidence, AnalysisProfiles,
│   │   │                    CodeQualityDetails, MetricsHistory, NewCodePanel
│   │   ├── issues/          Issues.tsx
│   │   │   └── components/   IssueReview, ProjectHeader et leurs CSS
│   │   ├── rules/           Rules.tsx
│   │   ├── quality-gates/   QualityGates.tsx
│   │   ├── administration/  Administration.tsx, AuditLog, AuthProviderSettings,
│   │   │                    CiTokens, DevOpsIntegrations, NewCodeSettings,
│   │   │                    Notifications, PlatformSettings, UsersAccess
│   │   └── account/         AccountTokens.tsx
│   ├── layouts/             AppShell.tsx, MainNavigation.tsx
│   ├── services/            api.ts, query-client.ts
│   ├── types/               api.ts
│   ├── utils/               branch-selection, evidence-links, metric-display,
│   │                        project-favorites et tests locaux
│   ├── workspace/           Workspace.tsx, AuthScreens.tsx, navigation-state.ts,
│   │                        useWorkspaceData.ts et tests locaux
│   ├── styles/              feuilles globales existantes
│   ├── styles.css           agrégateur CSS ordonné
│   ├── App.tsx              session et providers
│   └── main.tsx             point d'entrée DOM
└── tests/contracts/         tests transversaux
```

Créer un nouveau dossier seulement lorsqu'il porte du code réel ; colocaliser CSS et tests propres à une feature ou à un composant. `src/assets/`, `pages/`, `hooks/` et `store/` ne sont pas des dossiers à créer par convention.

## Conventions locales

- Imports : utiliser `@/` pour traverser plusieurs dossiers depuis `src/` (`paths` TypeScript et résolution native `tsconfigPaths` de Vite/Vitest). Garder `./` et `../` pour les voisins. Ne pas ajouter de barrels par feature ; `components/ui/index.ts` reste le seul barrel de primitives.
- Une feature n'importe pas une autre feature : remonter les éléments réellement partagés dans `components/common/`, `services/`, `types/` ou `utils/`. Ne pas créer de dossier vide ni de nouvelle abstraction pour un seul appelant.
- Préserver les `React.lazy` des écrans et sections d'administration et leurs chunks de build. TanStack Query reste la source de vérité du serveur ; `workspace/navigation-state.ts` gère l'URL sans React Router. `services/api.ts` utilise `fetch` sans Axios.
- Réutiliser `Button`, `TextBox`, `TextArea`, `ListBox`, `Checkbox` et `Grid` ; les contrôles natifs restent encapsulés dans `components/ui/`. Respecter les états absents, d'erreur et de chargement, les permissions, le clavier et les deux thèmes. Voir `../../DESIGN.md` pour les règles détaillées.
- Les ressources statiques de `public/` y restent. N'ajouter `src/assets/` qu'en cas d'import réel depuis le code ou le CSS. Conserver les tests `.test.ts`/`.test.tsx` proches du code et les contrats dans `tests/contracts/`.
- Documenter dans `../../docs/` tout changement de comportement ; si l'identité visuelle change explicitement, mettre aussi à jour `../../DESIGN.md` et les styles concernés.

## Validation (depuis `apps/web`)

| Commande           | Vérifie                                                        |
| ------------------ | -------------------------------------------------------------- |
| `npm ci`           | Installe les versions verrouillées si les dépendances manquent |
| `npm run check`    | TypeScript strict et tests Vitest                              |
| `npm run build`    | Bundle Vite et maintien des chunks chargés à la demande        |
| `npm run check:ci` | Variante CI avec rapports de couverture et JUnit               |

Depuis la racine, exécuter aussi `git diff --check`. Un build vert ne remplace pas la vérification des parcours navigateur/API concernés ; ne pas annoncer un contrôle visuel non effectué.
