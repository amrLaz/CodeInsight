---
title: Infrastructure Azure de CodeInsights
description: Terraform, PostgreSQL et déploiement Azure DevOps.
---

# Infrastructure Azure de CodeInsights

## Stack

Le module gère le groupe `up-france-odi-sast-prod-rg` dans Subscription Fabrik : App Service Linux B1, PostgreSQL Flexible Server 16 B1ms avec 32 Go et sauvegardes de 7 jours, Key Vault et droits de son identité managée. Le registre partagé `upgroup` est référencé comme source de données. Une image ASP.NET Core 10 sert l'API et React sur le port 8000.

Le backend Terraform est le blob `sast-platform-production.tfstate` du conteneur `sast-platform-devops-governance` dans `fdinfraterraformstates`. [backend-production.tfvars](variables/backend-production.tfvars) désigne cet état unique. Ne pas initialiser un nouvel état pour des ressources déjà gérées.

## Pipelines

- [CodeInsights Infrastructure — 963](https://dev.azure.com/up-group/Up.France.ODI/_build?definitionId=963) utilise [ci/azure-pipelines.yml](ci/azure-pipelines.yml). Il valide Terraform et produit un plan. Le paramètre `apply=true` applique ce plan sauvegardé. `recreateDatabase=true` force uniquement le remplacement de la base applicative et détruit son contenu : cette option exige une autorisation explicite de perte des données. Le garde-fou autorise aussi le retrait ponctuel des quatre secrets Azure OpenAI supprimés avec le backend correspondant ; toute autre suppression arrête la pipeline avant application.
- [CodeInsights Application — 969](https://dev.azure.com/up-group/Up.France.ODI/_build?definitionId=969) utilise [.azuredevops/deploy-apps.yml](../.azuredevops/deploy-apps.yml). Il vérifie .NET contre PostgreSQL isolé, construit React et l'API, publie l'image dans ACR, configure App Service et vérifie HTTPS, les deux routes de santé et la page React.

Les deux définitions ciblent `Up.France.CodeInsights`, branche `master`, et utilisent la connexion de service `Subscription Fabrik`. Exécuter l'infrastructure avant l'application lorsqu'une release change la configuration requise. Les triggers Terraform ne lancent qu'un plan ; l'application se déploie pour les changements applicatifs de `master`.

## Secrets et accès

Le Terraform provisionne aussi un compte Blob privé et le conteneur `reports`,
avec `Storage Blob Data Contributor` pour l'identité managée de l'application.
Les clés partagées et l'accès public des blobs sont désactivés ; les suppressions
disposent de sept jours de récupération. Les options `Ingestion__BlobServiceUri`
et `Ingestion__PlatformUrl` sont injectées dans App Service. Cette évolution a été
appliquée par le run 276081, sans suppression ni recréation de la base.
Le provider utilise Entra (`storage_use_azuread=true`) ; le rôle Blob de
l'identité de déploiement est initialisé avant le plan complet si nécessaire.
Les références de déploiement et le diagnostic sont dans [exploitation](../docs/exploitation.md).

Le groupe de variables `CodeInsights.Infrastructure` contient les valeurs secrètes PostgreSQL, bootstrap administrateur et signature JWT. Terraform écrit les secrets JWT et bootstrap dans Key Vault, référencés par l'API. Les anciens secrets Azure OpenAI sont retirés avec `v0.5.0`, puisque l'application ne maintient plus cette intégration. Ne jamais afficher ou versionner leur contenu. Les plans et l'état contiennent des valeurs sensibles même quand les sorties sont masquées.

L'identité système de l'application reçoit `AcrPull` et `Key Vault Secrets User`. Le principal de la connexion Terraform gère le coffre. La connexion PostgreSQL utilise un mot de passe ; la règle `AllowAzureServices` autorise les adresses Azure. Une isolation réseau privée et une identité base dédiée restent des évolutions à concevoir.

## Durcissement et risques acceptés

Les scanners IaC Trivy et Checkov de l'auto-analyse CodeInsights parcourent ce module. Il applique :

- Key Vault avec purge protection. Une fois appliquée, elle ne peut plus être désactivée : un coffre supprimé reste réservé pendant les sept jours de rétention.
- Secrets JWT et bootstrap typés `text/plain`, avec l'échéance `key_vault_secret_expiration_date` (défaut `2027-09-30T00:00:00Z`). Avant cette date, faire tourner les secrets dans le groupe de variables, repousser la date d'un an, puis appliquer. Changer la date met à jour l'attribut du secret sans recréer de ressource. Key Vault continue de servir un secret expiré, mais le scanner le signalera de nouveau.
- PostgreSQL avec `log_checkpoints`, `log_connections` et `connection_throttle.enable` à `on`. Ce sont des paramètres dynamiques, qui ne redémarrent pas le serveur.
- App Service sans publication FTP ni Web Deploy par authentification basique, avec FTPS désactivé, HTTP/2, messages d'erreur détaillés, traçage des requêtes en échec et journaux HTTP en système de fichiers (sept jours, 35 Mo).

Les autres constats sont des risques acceptés ou non applicables. Chacun est justifié en ligne par `#checkov:skip` ou `#trivy:ignore` sur sa ressource :

| Constat                                                                            | Raison                                                                                                                                  |
| ---------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| Accès public et absence de private endpoint (Key Vault, Blob, PostgreSQL, Web App) | Le réseau privé n'est pas conçu. App Service sans intégration VNet et agents hébergés passent par les endpoints publics, sous RBAC Entra ou pare-feu. |
| `AllowAzureServices` sur PostgreSQL                                                | Les IP sortantes d'App Service sont dynamiques.                                                                                         |
| Plan B1 mono-instance, sans redondance de zone                                     | Aucun engagement de service n'est pris (voir Validation).                                                                               |
| Sauvegardes PostgreSQL locales, Blob en LRS                                        | La géo-redondance PostgreSQL forcerait la recréation du serveur. Les rapports CI sont transitoires.                                      |
| Chiffrement d'infrastructure et CMK du compte Blob                                 | `infrastructure_encryption_enabled` forcerait la recréation du compte. Les clés gérées par Microsoft sont acceptées.                    |
| EasyAuth et certificat client sur l'App Service                                    | L'API authentifie elle-même navigateurs et CI (JWT, Entra). Les activer bloquerait l'ingestion CI.                                      |
| Journalisation Queue, montage Azure Files, nom du compte Blob                      | Sans objet : aucune file, aucun état local. Le nom calculé est conforme mais pas évaluable statiquement.                               |

Checkov signale encore `CKV2_AZURE_21` (journalisation des lectures Blob). Le corriger demande un paramètre de diagnostic vers un espace Log Analytics, que le module ne provisionne pas.

## Schéma et démarrage

Terraform provisionne le serveur et la base `sast_platform`. L'image exécute les migrations EF Core et le bootstrap avant d'ouvrir HTTP. Le verrou natif EF protège les migrations concurrentes. Une erreur arrête le démarrage ; l'image ne peut pas devenir saine avec un schéma non préparé. La commande `dotnet CodeInsights.Api.dll --migrate` exécute cette préparation seule.

Le bootstrap crée `admin@codeinsights.local` lorsqu'aucun administrateur n'existe. Son secret se trouve dans Key Vault sous `bootstrap-admin-password`. Les droits projet et organisation restent à configurer dans l'application.

## Validation

```powershell
terraform -chdir=infra fmt -check -recursive
terraform -chdir=infra validate -no-color
```

Le SDK, les tests et les limites des vérifications applicatives sont documentés dans [exploitation](../docs/exploitation.md#développement-local). Les sondes `/api/v1/health` et `/healthz` prouvent la disponibilité du processus ; un login et une lecture métier autorisée vérifient aussi l'accès à PostgreSQL. L'App Service utilise `/api/v1/health` comme chemin de santé.

Les tailles B1/B1ms ne constituent pas une preuve de capacité. Tester la charge, les restaurations PostgreSQL et les alertes avant de fixer des engagements de service. Le retour à une image validée doit respecter le schéma EF déjà appliqué.
