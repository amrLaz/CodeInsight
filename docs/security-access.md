---
title: Identités et autorisations
description: Matrice des accès utilisateur et CI de Up.France.CodeInsights.
---

# Identités et autorisations

Le hub distingue les sessions utilisateur, les jetons d'accès personnels et les tokens CI. Un utilisateur non administrateur voit les projets attribués directement, par organisation, par un groupe Entra transitif configuré ou par l'affectation explicite « tous les projets ». Un token CI ne peut agir que sur ses `project_keys` et ses scopes. Les contrôles sont toujours refaits côté API ; le rôle conservé dans le navigateur n'autorise aucune action.

L’implémentation ASP.NET Core 10 utilise [PlatformAuthenticationHandler](../apps/api/Auth/PlatformAuthenticationHandler.cs) et [AuthService](../apps/api/Auth/AuthService.cs), enregistrés par injection de dépendances. Les droits sont vérifiés sur les données de l’utilisateur à chaque requête. Un compte désactivé est refusé. Les mots de passe utilisent `PasswordHasher<PlatformUser>` d’ASP.NET Core. Les sessions sont des JWT HS256 validés par le handler JwtBearer (signature, émetteur, audience, expiration), configurés avec [SessionOptions](../apps/api/Auth/SessionOptions.cs). Les tokens CI sont opaques et seule leur empreinte SHA-256 est stockée.

Un **jeton d'accès personnel** est créé par l'utilisateur lui-même avec
`POST /api/v1/auth/me/tokens`. Il porte l'identité et les droits exacts de son
propriétaire, jamais davantage : les mêmes grants, les mêmes filtres et les mêmes
refus s'appliquent. Sa valeur n'est renvoyée qu'à la création, seule son empreinte
SHA-256 est conservée, son expiration est obligatoire et bornée à 365 jours, et un
propriétaire ne peut pas détenir plus de dix jetons actifs. Il est refusé dès que le
compte est désactivé, le jeton révoqué ou expiré.

Sa portée est volontairement étroite. Il est en lecture seule : seules les méthodes
`GET` et `HEAD` sont acceptées, sauf si le scope `collaboration:write` a été demandé
à la création, qui autorise alors les écritures ouvertes à un utilisateur autorisé
— assignation, commentaire et décision de revue. Les seuls scopes acceptés sont
`analysis:read` et `collaboration:write`. Aucun jeton personnel ne satisfait une
route d'administration, même si son propriétaire est administrateur, et aucun jeton
ne peut en créer ou en révoquer un autre : la gestion des jetons exige une session
interactive. La liste `GET /api/v1/auth/me/tokens` n'expose que les quatre derniers
caractères ; `DELETE /api/v1/auth/me/tokens/{token_id}` révoque, de façon idempotente,
un jeton du seul propriétaire. Création et révocation sont journalisées dans l'audit.

## Matrice des routes

| Famille                                                                                            | Anonyme | Utilisateur avec attribution   | Token CI du projet  | Administrateur                 |
| -------------------------------------------------------------------------------------------------- | ------- | ------------------------------ | ------------------- | ------------------------------ |
| Santé, méthodes de connexion, login local, login/callback Entra, catalogue public des fournisseurs | Oui     | Oui                            | Oui                 | Oui                            |
| Projets, analyses, findings, règles, gate et exports du projet                                     | Non     | Lecture                        | `analysis:read`     | Lecture/gestion                |
| Jetons personnels de l'utilisateur `/api/v1/auth/me/tokens`                                        | Non     | Session interactive uniquement | Non                 | Session interactive uniquement |
| Politiques SLA `/api/v1/sla` (globale et par projet)                                               | Non     | Lecture                        | Non                 | Lecture/écriture               |
| Work item d'un finding `/collaboration/findings/{id}/work-item`                                    | Non     | Lecture et demande             | Non                 | Lecture et demande             |
| Réglages work items `/api/v1/work-items/projects/{projectKey}`                                     | Non     | Lecture                        | Non                 | Lecture/écriture               |
| Tendances `/api/v1/insights/portfolio`, limitées aux projets autorisés                             | Non     | Lecture                        | Non                 | Lecture                        |
| Politique de licences `/api/v1/licenses/policy` et inventaire `/unclassified`                      | Non     | Lecture de la politique        | Non                 | Lecture/écriture et inventaire |
| `POST /api/v1/analyses`                                                                            | Non     | Non                            | `analysis:write`    | Non, utiliser un token CI      |
| Métadonnées `PUT /api/v1/integrations/projects/{project_key}`                                      | Non     | Non                            | `integration:write` | Oui                            |
| Organisations, utilisateurs, grants, tokens, paramètres, intégrations ADO, notifications et audit  | Non     | Non                            | Non                 | Oui                            |
| Suppression de règles ou de données                                                                | Non     | Non                            | Non                 | Oui                            |

Les accès directs par identifiant d'analyse retrouvent d'abord le `project_key`, puis appliquent la même vérification que les listes. Une collection globale est filtrée aux projets autorisés.

## Administration

Les organisations CodeInsights sont facultatives : elles regroupent des projets afin d'accorder leur accès en une fois et sont distinctes de la configuration d'organisation Azure DevOps. La production provisionne actuellement les projets sans créer ces regroupements ni renseigner leur `organization_id`, donc le catalogue peut être vide. Les comptes locaux historiques conservent `PUT /api/v1/auth/users/{user_id}/grants`. Pour Entra, rechercher l'annuaire avec `GET /api/v1/auth/directory-principals?search=...` (`kind=all` par défaut, `user|group` conservé pour compatibilité), consulter les affectations avec `GET /api/v1/auth/access-principals`, puis remplacer atomiquement rôle, statut et grants par `PUT /api/v1/auth/access-principals/{kind}/{object_id}`. Une écriture avec `organization_ids: []` et `project_keys: []` persiste un grant interne `all/*`; elle signifie « tous les projets » sans confondre ce cas avec une identité Entra créée par une première connexion et pas encore configurée. La recherche mixte exécute en parallèle deux appels Graph limités à huit résultats, partage le même jeton, exclut les groupes à appartenance masquée, trie puis limite la réponse à douze éléments sans continuation. Les types d'écriture acceptés sont `entra-user` et `entra-group`; l'API recharge le nom, l'email et l'état depuis Graph avant l'écriture.

Les tokens n’obtiennent aucun accès implicite. Leur affectation se fait avec `PUT /api/v1/platform/token/{token_id}/access`. Les nouveaux tokens exigent au moins un projet et reçoivent explicitement les scopes `analysis:read`, `analysis:write` et `integration:write`. Plusieurs tokens actifs peuvent coexister ; la révocation est individuelle.

## Entra ID

Le flux Authorization Code vérifie `state`, le cookie HTTP-only lié au navigateur, le nonce, la signature RS256 issue des JWK Microsoft, l'émetteur, le tenant, l'audience et les dates. La transaction est consommée atomiquement et ne peut pas être rejouée. `GET /auth/entra/login?mode=silent` ajoute `prompt=none`; `login_required`, `interaction_required` et `consent_required` rendent la main sans alerte, tandis que les autres erreurs restent visibles. L'identité est liée au couple tenant/object ID ; un email identique ne relie jamais automatiquement un compte local. Un nouveau compte Entra reçoit le rôle `default` et aucun grant. L'URL de retour utilise HTTPS ; HTTP est accepté uniquement sur `localhost`, `127.0.0.1` ou `::1` pour le développement local.

Microsoft Graph reçoit un jeton applicatif `client_credentials` pour `https://graph.microsoft.com/.default`, distinct des scopes OIDC délégués `openid profile email`. `User.Read.All` couvre la recherche des utilisateurs et leurs photos ; `GroupMember.Read.All` couvre les groupes et les appartenances transitives. La photo 48 × 48 du compte connecté passe par `/auth/me/photo`; celle d'un utilisateur Entra déjà configuré passe par la route administrateur `/auth/access-principals/entra-user/{object_id}/photo`. Cette route vérifie l'affectation avant l'appel Graph. Les groupes ne déclenchent aucun appel photo et aucune permission supplémentaire n'est requise. Les continuations Graph sont validées sur l'origine et le chemin attendus, toutes les pages d'appartenance sont lues, et les photos sont limitées à 1 Mio et à un type `image/*`, avec un cache privé de cinq minutes. Les appartenances sont cachées cinq minutes uniquement après succès. Une panne ultérieure n'utilise pas une valeur expirée : rôle et grants directs restent seuls actifs avec le statut `unavailable`. Les droits sont additifs et le rôle le plus élevé gagne. Les groupes désactivés ou à appartenance masquée ne contribuent pas.

En production, `Authentication:LocalAccountsMode=bootstrap-only` masque le formulaire local et refuse création, réinitialisation et modification du compte de secours. `admin@codeinsights.local` reste utilisable directement par l'API pour les pipelines et urgences, mais apparaît en lecture seule dans l'administration. Le développement conserve `enabled` par défaut.

[EntraService](../apps/api/Auth/EntraService.cs) porte ce flux et utilise Microsoft IdentityModel pour la validation cryptographique. Les checks remplacent les services externes ; ils ne constituent pas une preuve de connexion réelle au tenant. Les secrets Entra et SMTP sont masqués dans les réponses d'administration ; une mise à jour vide conserve le secret déjà enregistré. Les appels sortants Azure DevOps utilisent un jeton Entra court acquis par l'identité managée système de l'App Service et aucun PAT Azure DevOps n'est accepté ou exposé par l'API. Les événements de gestion sont ajoutés via [AuditService](../apps/api/Services/AuditService.cs).
