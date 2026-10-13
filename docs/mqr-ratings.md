---
title: Ratings MQR et gate New Code
description: Calcul, complétude et restitution des ratings A–E du profil .NET version 2.
---

# Ratings MQR et gate New Code

Le profil immuable `upcoop-dotnet/2` applique le modèle MQR avec la formule
`sonar-mqr/v1`. Les ratings sont calculés par l'API après normalisation des
rapports, puis figés dans le snapshot de l'analyse. Les anciens champs
`*_score` restent lisibles pour la compatibilité des analyses version 1 ; ils ne
sont jamais convertis en lettres ni utilisés par le gate version 2.

## Source de vérité des règles

Le catalogue du profil associe chaque règle à l'identité fonctionnelle
`(outil, ruleId)`, à une catégorie, une criticité et, pour les code smells, un
coût de remédiation. `tool_version` reste obligatoire dans le catalogue et les
manifestes afin de conserver la provenance observée, mais ne sélectionne jamais
une définition de règle et ne limite pas les versions majeures acceptées. Le
pilote attend :

- un unique SARIF `roslyn`, avec la version du SDK réellement résolue par le projet ;
- Semgrep avec le ruleset `upcoop-dotnet-mqr/v1` embarqué dans
  `UpCoopSecurityScan@2` ;
- `dotnet-coverage`, `jscpd`, `cloc`, `lizard`, `trivy` et `gitleaks` selon le
  manifeste du profil.

Un catalogue ne peut pas définir deux fois le même `(outil, ruleId)`, même avec
des versions d'outil différentes. Un ancien snapshot ambigu reste incomplet :
le serveur ne choisit jamais arbitrairement une définition. Une règle non
cataloguée ne reçoit pas une catégorie par wildcard. Elle rend la
dimension MQR concernée incomplète au lieu de produire une note optimiste. Les
vulnérabilités de dépendances issues de Trivy/CycloneDX restent une dimension SCA
séparée ; elles ne sont pas mélangées à la Security rating. Gitleaks conserve sa
catégorie `secret`, également distincte.

Le catalogue Roslyn couvre aussi les diagnostics réellement observés pendant le
pilote .NET 10. `ASP0000` est un bug `High`. `CA1848`, `ASP0027`, `CA1050`,
`CA1068`, `CA1859`, `CA1860`, `CA1861`, `CA1862`, `CA1873`, `CS9236` et
`SYSLIB1045` sont des code smells `Low` avec un coût constant explicite. Le
producteur partagé retire le préfixe de chemin ajouté par Semgrep aux règles
locales avant publication du SARIF.

Une seconde correction, observée sur un rapport Roslyn réel de
`Up.France.ODI.Services.Commercant.BO.Web` (projet CommercantBO, MudBlazor),
ajoute `CA2016` et `CS0649` en bug `Medium`, `ASP0015`, `CA1000`, `CA1051`,
`CA1824`, `CS0169` et `MUD0010` en code smell `Low` avec coût constant. Deux
règles de ce lot — `RZ10012` (diagnostic du compilateur Razor, classé bug
`Medium`) et `MUD0010` (analyseur MudBlazor, code smell `Low`) — sont une
estimation de meilleur effort, non vérifiée indépendamment contre la
documentation de l'outil qui les émet ; à corriger si leur sémantique réelle
diffère.

Une troisième correction, observée sur un rapport Roslyn réel de
`Up.France.ODI.Services.Financeur` (l'API, premier pilote .NET sur agent
Windows), ajoute `ASP5001` (usage d'API `CompatibilityVersion` obsolète) et
`CA2254` (template de message de log non constant entre appels) en code smell
`Low` avec coût constant.

Une quatrième correction, observée sur un rapport Roslyn réel de
`Up.France.ODI.Services.Financeur.BMF` — un projet distinct du précédent, dont
le jeu de règles observées diffère —, ajoute `CS8629` (« Nullable value type
may be null ») en bug `Medium`, même famille que les autres avertissements
nullable déjà catalogués (`CS8600`, `CS8601`, `CS8603`, `CS8618`, `CS8625`).

Une cinquième correction, observée sur un rapport Roslyn plus complet du même
projet (`Up.France.ODI.Services.Financeur.BMF`, une fois le WebApi inclus dans
la compilation), ajoute `CA1806` (résultat de `TryParse` ignoré), `CA1822`
(membre à marquer `static`) et `CA1869` (`JsonSerializerOptions` à mettre en
cache) en code smell `Low` avec coût constant.

Une sixième correction, observée sur le même projet, ajoute `CA1829`
(`Count()` au lieu de `Length`/`Count`) et `CA1866` (surcharge
`StartsWith(string)` au lieu de `StartsWith(char)`) en code smell `Low` avec
coût constant, même famille que `CA1824`.

Une septième correction, observée sur `Up.France.ODI.Services.Commercant`
(branche `integration/tst`), ajoute `CS0472` et `CS8073` (comparaison
toujours fausse d'un type valeur avec `null`) en bug `Medium`, `CS9179`
(paramètre de primary constructor masqué par un membre de base) en bug
`Medium`, `CS8632` (annotation nullable hors contexte `#nullable`) en code
smell `Low`, et cinq règles `AVxxxx` de l'analyseur tiers
`Asp.Versioning.Mvc.ApiExplorer` (`AV0011`, `AV0013`, `AV0015`, `AV0016`,
`AV0020`, des recommandations de configuration de versionnage d'API) en code
smell `Low` — première apparition de cet outil dans le catalogue, classification
non vérifiée indépendamment contre sa documentation. Ajoute aussi
`ASPDEPR006` (usage de `IActionContextAccessor`, obsolète) en code smell
`Low` — nouvelle famille de préfixe ASP.NET Core (`ASPDEPR`, dépréciations),
distincte du préfixe `ASP` déjà catalogué.

Trivy n'était référencé dans aucune catégorie du catalogue `upcoop-dotnet/2`
jusqu'ici (à la différence de Semgrep) : ses contrôles de configuration
Terraform (`AZU-*`, observés sur `Up.France.ODI.Services.Commercant`) ne
bloquaient aucune note MQR mais restaient aussi exclus du compteur brut
`vulnerabilities`/`security_hotspots`, puisque ce compteur ne compte que les
findings déjà classés. `AZU-0001`, `AZU-0015` et `AZU-0017` sont ajoutées en
`security_hotspot`, même rôle que les contrôles IaC de Checkov ; les CVE de
dépendances détectées par Trivy restent une dimension SCA séparée, non
mélangée à cette classification.

Ce cycle réactif (une règle ajoutée à la fois, découverte projet par projet) a
une limite structurelle : chaque nouveau pilote avec un style de code
différent fait remonter de nouvelles règles jamais vues. `upcoop-dotnet/2`
importe donc désormais en une fois l'intégralité des règles publiées par
Microsoft.CodeAnalysis.NetAnalyzers, générée depuis les fichiers
`AnalyzerReleases.Shipped.md`/`AnalyzerReleases.Unshipped.md` du dépôt
`dotnet/roslyn-analyzers` (source faisant autorité, pas une estimation) et
embarquée dans `apps/api/Resources/dotnet-analyzer-rules.json`. La catégorie
officielle Microsoft de chaque règle est mappée vers l'une de nos trois
catégories : `Security` → `vulnerability` (sévérité `High`), `Reliability` →
`bug` (`Medium`), toutes les autres (`Design`, `Performance`, `Naming`,
`Globalization`, `Maintainability`, `Interoperability`, `Documentation`,
`Publish`, `Usage`) → `code_smell` (`Low`, coût de remédiation constant de 5
minutes). Une règle CA déjà classée à la main ci-dessus reste prioritaire :
l'import en masse exclut tout identifiant déjà présent pour ne jamais produire
de doublon `(outil, ruleId)`. Les règles ASP.NET Core, MudBlazor et Razor
restent hors de ce référentiel (Microsoft ne les publie pas dans ce même
format) et continuent d'être ajoutées au cas par cas comme précédemment.
Régénérer ce fichier lors d'une nouvelle version majeure de l'analyseur .NET
en retéléchargeant les deux fichiers source et en réexcluant les identifiants
déjà catalogués à la main.

Une règle manquante sur une métrique **requise** par le profil ne se limite pas
à afficher `-` sur cette note : elle rend toute l'analyse `technical_state:
incomplete` (`GateEvaluation.TechnicalState`,
[GateEvaluation.cs](../apps/api/Services/GateEvaluation.cs)), ce qui fait
échouer la tâche pipeline `CodeInsights: publish reports` (code de sortie 1,
`publish_reports.py`). Observé sur `Up.France.ODI.Services.Commercant.WebApi`
(branche `feature/integrate-codeinsights`, première intégration du projet) :
`CS0105` (directive `using` dupliquée) manquait au catalogue, Security/
Reliability/Maintainability restaient `not_measured`, et la pipeline
échouait — alors que l'ingestion elle-même s'était terminée sans erreur
(`Analysis: completed` dans le hub) : `technical_state` est un verdict de
complétude des données, distinct du statut de traitement de l'ingestion.
Ajoutée en code smell `Low`, coût constant de 2 minutes, même famille que
`CS8632`.

Ce cas a déclenché le même traitement que les règles `CA*` : `upcoop-dotnet/2`
importe désormais aussi en une fois tous les avertissements actifs du
compilateur C# (`WRN_*` dans
[`src/Compilers/CSharp/Portable/Errors/ErrorCode.cs`](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Errors/ErrorCode.cs)
du dépôt `dotnet/roslyn`, source faisant autorité), embarqué dans
`apps/api/Resources/csharp-compiler-warnings.json`. Contrairement aux règles
`CA*`, Microsoft ne publie pas de taxonomie de catégories pour les
diagnostics du compilateur : chaque entrée importée est donc classée par
défaut `code_smell`/`Low`/5 minutes, la classification la plus prudente.
Les identifiants `CS*` déjà classés à la main ailleurs dans ce fichier
(`CS0472`, `CS8073`, `CS9179`, `CS8629`… — ceux dont l'examen a montré qu'ils
indiquent une vraie erreur logique, pas juste du nettoyage) restent
prioritaires et sont exclus de l'import. Les diagnostics `ERR_*`/`FTL_*` ne
sont pas importés : un build qui produit un rapport SARIF a par définition
compilé, ces codes ne peuvent donc jamais apparaître comme finding. Régénérer
ce fichier en retéléchargeant `ErrorCode.cs` et en réexcluant les
identifiants déjà catalogués à la main, même méthode que pour les règles
`CA*`. Restent en dehors des deux imports en masse, toujours au cas par cas :
ASP.NET Core, MudBlazor, Razor et tout analyseur tiers (`Asp.Versioning.Mvc`,
etc.) sans taxonomie publiée exploitable.

Premier test réel de ces deux imports en masse sur `Up.France.ODI.Services.Commercant`
(branche `feature/integrate-codeinsights`) : sur 23 règles Roslyn déclenchées,
21 étaient déjà couvertes automatiquement (`CA*` et `CS*` confondus). Seules
`SYSLIB0022` (chiffrement `Rijndael`/`RijndaelManaged` obsolète) et
`SYSLIB0041` (constructeurs `Rfc2898DeriveBytes` aux valeurs par défaut
faibles — itérations/algorithme de hachage) manquaient, ni l'un ni l'autre
publié dans un format structuré par Microsoft (diagnostics `[Obsolete]`
ponctuels du framework, pas une release d'analyseur). Les deux signalent une
vraie faiblesse cryptographique, pas du nettoyage : ajoutées en
`vulnerability`/`High`, même sévérité que `CA5379` (catégorie `Security` de
l'import en masse) pour rester cohérent.

Le catalogue `upcoop-react/2` (`ReactMqrProfile`, curé depuis un pilote réel
sur `Up.France.ODI.Services.Commercant.Web` — 176 findings ESLint sur 10
règles) ne référençait que Semgrep et Checkov ; Gitleaks et Trivy n'y avaient
aucune entrée. Observé sur ce même projet : Gitleaks a remonté
`generic-api-key`, `jwt` et `private-key`, Trivy `private-key` — de vraies
détections de secret, non classées, qui suffisaient à rendre toute l'analyse
`technical_state: incomplete` malgré les 10 règles ESLint déjà correctement
couvertes.

Plutôt que d'ajouter ces quatre règles une à une, `upcoop-react/2` **et**
`upcoop-dotnet/2` importent désormais en masse l'intégralité des règles de
détection de secret de Gitleaks (222, `gitleaks/config/gitleaks.toml`,
source faisant autorité — c'est le fichier que le conteneur
`UpCoopSecurityScan@2` exécute réellement) et de Trivy (106, son sous-système
secrets `pkg/fanal/secret/builtin-rules.go`, distinct de ses contrôles
Terraform `AZU-*` déjà catalogués et de son scan de CVE de dépendances,
dimension SCA séparée). Ni Gitleaks ni Trivy ne publient de taxonomie de
catégories pour ces règles : contrairement à l'import `CA*`, il n'y a donc
aucun mapping à faire — chaque entrée reçoit uniformément `secret`/`Medium`,
la même classification que les règles `CKV_SECRET_*` de Checkov déjà
présentes. `upcoop-dotnet/2` reçoit le même import de façon proactive : ces
deux scanners tournent à l'identique sur tous les stacks via le même
conteneur de sécurité, donc l'absence de couverture y était le même piège
latent, simplement pas encore déclenché. Régénérer les deux fichiers
`apps/api/Resources/gitleaks-rules.json` et `.../trivy-secret-rules.json` en
retéléchargeant leurs sources respectives.

Une fois ces trous comblés, `Security`/`Reliability`/`Maintainability`/
`Coverage`/`Duplications` se sont mesurés correctement sur
`Up.France.ODI.Services.Commercant.Web` (`master`) — mais `Security review`
restait bloqué sur `-`, raison affichée : « the profile contains no
applicable rule ». Contrairement aux trous précédents, ce n'est pas une
règle réellement observée et non classée : `upcoop-react/2` n'a **aucune**
règle en catégorie `security_hotspot` dans tout son catalogue (à la
différence d'`upcoop-dotnet/2`, qui a Checkov Terraform et Semgrep
`permissive-cors`) — les entrées Checkov de ce profil sont classées
`secret`, pas `security_hotspot`. Un seuil de revue de hotspots contre zéro
hotspot possible ne peut structurellement jamais se résoudre : ce n'est pas
une mesure en attente, c'est une configuration insatisfiable par
construction. `SecurityReview` est donc désactivé sur `upcoop-react/2`
(`new(false, null, "new_code")`) jusqu'à ce qu'un vrai finding
`security_hotspot` soit observé sur un pilote React à classer.

Un pilote bien plus chargé sur `Up.France.ODI.Services.Financeur` (repo
Terraform-Azure avec une vraie spec OpenAPI) a révélé qu'`upcoop-dotnet/2`
n'avait en réalité **aucune** entrée Checkov du tout — seuls Semgrep et
trois `AZU-*` Trivy (`AZU-0001/0015/0017`) y figuraient — et que la liste
Trivy Terraform manquait déjà 5 règles observées (`AZU-0002/0003/0005/0071/0072`).
Mêmes symptômes que Gitleaks/Trivy secrets : ni Checkov ni Trivy ne publient
de taxonomie de catégories pour leurs contrôles Terraform/OpenAPI, donc
import en masse plutôt que cas par cas :
- **Checkov** (263 règles) : uniquement les répertoires `terraform/checks/resource/azure`
  et `openapi/checks` de `bridgecrewio/checkov` — les ~15 autres fournisseurs
  (AWS, GCP, Kubernetes, Dockerfile...) sont hors périmètre, aucune config de
  ce scanner ne peut jamais les déclencher. Classées uniformément
  `security_hotspot`/`Low`, comme les `CKV_SECRET_*` avant elles.
- **Trivy Azure** (73 règles, `aquasecurity/trivy-checks`' `checks/cloud/azure/**.rego`) :
  remplace les 3 entrées `AZU-*` manuelles. Contrairement à Gitleaks/Checkov,
  chaque règle Trivy porte sa propre métadonnée `severity` dans le code
  source (`# custom: severity: CRITICAL/HIGH/MEDIUM/LOW`) — préservée plutôt
  que d'y appliquer une sévérité par défaut uniforme.

La première analyse de `Up.France.CodeInsights` a montré que cet import Checkov
avait oublié les **contrôles de graphe** : six findings `CKV2_AZURE_*`
(`1`, `21`, `26`, `32`, `33`, `57`) restaient `unknown` et rendaient la revue
sécurité non mesurée (`unclassified_rules`), alors que 43 hotspots étaient
comptés. Le catalogue ajoute donc les 60 contrôles de
`terraform/checks/graph_checks/azure/*.yaml` (Checkov 3.3.16) : 56 `CKV2_AZURE_*`
et 4 `CKV_AZURE_*` qui n'existent que sous cette forme (`23`, `24`, `119`, `120`),
soit 323 règles Checkov, même classification `security_hotspot`/`Low`. Les
analyses déjà publiées gardent leur verdict ; seule une nouvelle analyse en
bénéficie.

Régénérer `apps/api/Resources/checkov-terraform-azure-openapi-rules.json` en
re-parcourant `terraform/checks/resource/azure`, `terraform/checks/graph_checks/azure`
(champ `id` de chaque YAML) et `openapi/checks`, et
`.../trivy-azure-misconfig-rules.json` en re-parcourant son répertoire.

### Catalogue `upcoop-php/1`

`upcoop-php/1` est un profil MQR : ses conditions portent sur les ratings et la
densité de duplication, comme `upcoop-react/2`. Il a été mis à jour en place le
12 octobre 2026, le jour de sa création, alors que seules les deux analyses
pilotes existaient ; les snapshots déjà enregistrés conservent leur définition
d'origine. Le catalogue (`PhpMqrProfile`) contient 1 791 règles :

| Outil | Source | Classification |
| --- | --- | --- |
| PHPStan 2.3 et Larastan 3.13 (1 187) | `website/src/errorsIdentifiers.json` de phpstan/phpstan (branche 2.3.x), identifiants `larastan.*` du code de Larastan, repli `phpstan.unidentified` | bug `Medium` à 10 min si PHP lève une `Error`, une `TypeError` ou une erreur fatale (symbole inconnu, nombre ou type d'arguments, appel sur un non-objet, visibilité, `readonly`, `return` manquant) ; sinon code smell `Low` à 5 min, y compris tout identifiant émis uniquement par les règles `PhpDoc`, `Deprecations`, `DeadCode` ou `Generics` |
| Semgrep (180) | rulesets du registre `p/ci` et `p/php` | `subcategory: vuln` → vulnerability, `audit` → security hotspot ; `ERROR` → High, `WARNING`/`MEDIUM` → Medium, `INFO` → Low |
| Gitleaks, Trivy, Checkov (secrets) | imports en masse existants | secret `Medium` |
| Trivy `DS-*`, Checkov `CKV_DOCKER_*`/`CKV2_DOCKER_*` | imports Dockerfile existants | security hotspot, comme `upcoop-dotnet/2` |
| Checkov `CKV_OPENAPI_*` (21) | import Terraform-Azure/OpenAPI existant | security hotspot `Low`, comme `upcoop-dotnet/2` ; ajouté le 13 octobre 2026 après `CKV_OPENAPI_4/5/21` non classés sur BmscServices (build 283294) |

La règle de classification PHPStan a été relue sur les 52 identifiants observés
sur les pilotes LogiwebServices (build 283004) et CardsServices (build 283076) :
`parameter.notFound` (balise `@param` inconnue) reste un code smell et
`method.staticCall` (appel statique d'une méthode d'instance, `Error` en PHP 8)
devient un bug. Les 925 findings de ces deux builds sont tous classés. Les
ressources sont `apps/api/Resources/phpstan-rules.json` et
`semgrep-ci-php-rules.json`. Pour les régénérer : télécharger le fichier
d'identifiants PHPStan de la branche correspondant à la version utilisée,
extraire les identifiants `larastan.*` du dépôt Larastan, retélécharger
`semgrep.dev/c/p/ci` et `semgrep.dev/c/p/php`, puis appliquer les règles du
tableau. La revue sécurité est active (100 % des hotspots du New Code), puisque
le catalogue contient des security hotspots.

## Calcul et états

| Rating          | Preuve utilisée                                   | Calcul                                                                                |
| --------------- | ------------------------------------------------- | ------------------------------------------------------------------------------------- |
| Security        | vulnérabilités actives, hors exceptions acceptées | pire criticité : A aucune au-dessus de Info, B Low, C Medium, D High, E Blocker       |
| Reliability     | bugs actifs                                       | même échelle de pire criticité                                                        |
| Maintainability | code smells actifs et coûts catalogués            | dette / (`lignes de code × 30 min`) : A ≤ 5 %, B < 10 %, C < 20 %, D < 50 %, E ≥ 50 % |
| Security review | hotspots dans le périmètre                        | part revue : A ≥ 80 %, B ≥ 70 %, C ≥ 50 %, D ≥ 30 %, E < 30 %                         |

Chaque résultat expose la lettre éventuelle, l'état `measured`, `not_measured`,
`failed` ou `not_applicable`, les compteurs utilisés, la cause et la version de
formule. Zéro finding après une exécution réelle est une mesure et produit `A`.
Un analyseur absent ou échoué, aucune règle applicable, une classification
incomplète ou un coût de remédiation manquant produit une valeur indisponible,
affichée `-`, avec sa cause accessible. Un hotspot n'est pas une vulnérabilité ;
sa décision de revue est évaluée séparément.

## Overall Code et New Code

Les quatre ratings sont calculés pour le code complet et pour le New Code. Le
périmètre New Code provient de la comparaison Git figée avec l'analyse. Sans
baseline exploitable, tous ses ratings restent indisponibles. Les exceptions
acceptées sont exclues dans les deux périmètres. Cette absence maintient le gate
`unknown`, sans transformer l'analyse en échec technique lorsque les mesures
Overall requises sont complètes.

La compatibilité des producteurs compare `(outil, format, format_version)` : une
montée de version de Roslyn, Semgrep ou d'un autre outil conserve la baseline,
alors qu'un changement de version du format natif la rend incompatible. La
configuration exécutable suit la même règle de provenance : son empreinte porte
sur l'outil, le chemin et le contenu, pas sur `tool_version`.

Quand plusieurs occurrences partagent un fingerprint SARIF, le comparateur les
distingue par règle, chemin, message et ligne Git mappée. Il conserve l'état
`indeterminate` si ces preuves ne permettent toujours pas une correspondance
unique.

### Définition de la baseline

La définition New Code est persistée séparément du périmètre de code. Sa
résolution suit l'ordre `cible de pull request`, override CI
`new_code_reference_branch`, branche, projet, global, puis le défaut
`previous_version`. La configuration autorise uniquement la matrice suivante :

| Niveau | Modes autorisés |
| --- | --- |
| global | `previous_version`, `number_of_days` |
| projet | `previous_version`, `number_of_days`, `reference_branch` |
| branche | les trois précédents et `specific_analysis` |

`number_of_days` accepte de 1 à 90 jours. La baseline est la dernière analyse
terminée au plus tard à la date UTC courante moins ce nombre de jours ; aucune
analyse assez ancienne produit `number_of_days_baseline_absent`, sans repli
silencieux. `previous_version` sélectionne la dernière analyse terminée de la
même branche et du même dépôt dont `project_version` diffère. Une version
absente ou aucune version antérieure distincte reste explicitement
indisponible. `reference_branch` prend le dernier snapshot terminé de la branche
choisie ; ce snapshot peut être en retard sur l'état Git vivant de cette
branche. `specific_analysis` n'est admis qu'au niveau branche et exige une
analyse terminée du même projet, dépôt et branche. Une branche ne peut jamais se
prendre elle-même comme référence ; au niveau projet, la branche de référence
ne peut pas être la `main_branch` du projet.

Contrairement à SonarQube, CodeInsights ne date pas les lignes à partir du blame
SCM et ne cherche pas à reproduire une période calendaire ligne par ligne. Il
compare deux snapshots publiés à des commits exacts. Le mode
`previous_version` inclut donc tout ce qui sépare le dernier snapshot d'une
version distincte du snapshot courant. Seul le diff Git publié entre les SHA
résolus fait foi. Le périmètre de code conserve encore ses anciens champs de
référence pour la lecture des configurations historiques, mais ils ne
sélectionnent plus la baseline New Code.

La résolution est figée dans `ingestions.new_code_snapshot`. La finalisation la
recalcule ; une définition ou une baseline ayant changé entre création et
finalisation renvoie 409 et oblige le producteur à résoudre puis republier. La
réponse de comparaison expose la définition et sa source effectives, l'analyse,
le commit, la branche, la version et la date de baseline, ou une raison
d'indisponibilité précise.

Pendant la transition des producteurs `v3.1.0`, l'ingestion seule accepte encore
un `target_sha` accompagné d'un `git_diff` cohérent sans `project_version`. Ce
chemin `legacy_manifest_target` n'est utilisé que si aucune définition New Code,
aucun override et aucun contexte PR ne s'appliquent, et uniquement si une
analyse terminée du même projet et dépôt existe exactement à ce SHA. Il n'est
pas exposé comme repli de `/new-code/resolve` et disparaît dès qu'un projet adopte
la configuration dédiée.

La couverture combine lignes et conditions :

```text
coverage = (lignes couvertes + conditions couvertes)
         / (lignes à couvrir + conditions à couvrir) × 100
```

Quand un rapport Cobertura mesure les lignes exécutables mais ne fournit aucune
preuve de conditions (cas du convertisseur `dotnet-coverage` sur la sortie MTP),
`coverage` reprend la couverture de lignes. `branch_coverage` et
`condition_coverage` restent indisponibles : elles ne sont jamais déduites du
seul taux de lignes.

Pour le New Code, la couverture est calculée uniquement sur les lignes ajoutées
qui existent réellement dans LCOV/Cobertura. Un diff mêlant code, documentation
et CSS n'exige donc pas que les fichiers non exécutables apparaissent dans le
rapport. Si le producteur requis est absent ou échoué, la condition reste
`unknown` avec `coverage_producer_incomplete`. Si le rapport est présent mais
qu'aucune ligne ajoutée n'est exécutable, la condition est `not_applicable` avec
`no_changed_executable_lines`, jamais `0 %`.

Les exclusions de couverture restent volontairement configurées chez le
producteur (`ExcludeFromCodeCoverage`, filtres `dotnet-coverage`,
`coverage.include` et `coverage.exclude` de Vitest). CodeInsights importe le
rapport standard et ne maintient pas une seconde liste d'exclusions centrale ;
c'est un écart assumé avec SonarQube.

La densité de duplication utilise l'union des lignes présentes dans les blocs
jscpd afin de ne compter une ligne qu'une fois :

```text
duplicated_lines_density = lignes dupliquées / lignes de code × 100
```

Sur le New Code, le numérateur est limité aux lignes ajoutées présentes dans un
bloc dupliqué et le dénominateur aux lignes ajoutées connues de la comparaison
Git. Cette provenance est conservée dans le snapshot `code_quality`.

## Gate du profil `upcoop-dotnet/2`

Le gate porte uniquement sur le New Code : Security, Reliability et
Maintainability rating à `A`, Security review à `100 %`, couverture à au moins
`80 %` et densité de duplication à au plus `3 %`. Pour moins de 20 lignes
ajoutées, seules la couverture et la duplication sont `not_applicable`. Toute
autre mesure requise indisponible rend le verdict `unknown`, jamais `pass` ;
l'exemption de petit changement reste `not_applicable` jusqu'au verdict final.

L'API persiste les quatre lettres sur `analysis_records` pour les listes et
conserve, dans `gate_snapshot`, le détail Overall/New Code, les revues, les
formules, les compteurs, les causes et les conditions évaluées. L'interface
affiche ces lettres sans les mélanger aux anciens scores `/100`, détaille les
preuves sous chaque carte et utilise une échelle A–E dans l'historique.

## Déploiement du producteur

Le dépôt partagé `Up.France.ODI.AzurePipelines` contient le producteur corrigé :
un SARIF Roslyn réel, fusionné à partir des compilations des projets, sans faux
rapport `roslyn-bugs`. Son test de contrat compile un fixture qui déclenche un
vrai diagnostic `CS8602`.

Le producteur MQR est publié par le tag `v3.1.0` du dépôt partagé et utilise
l'extension `0.5.0`. La pipeline CodeInsights est épinglée sur ces versions.
L'exécution de la pipeline pilote et le déploiement en production restent des
preuves distinctes des checks locaux. Le déploiement conserve les probes HTTP/API
bornées, puis publie l'analyse via la connexion `CodeInsights-Coop`.

La pipeline de déploiement ne lance plus Chromium, ne crée plus de session
navigateur temporaire et ne publie plus l'artefact `codeinsights-mqr-ui`. La
validation visuelle authentifiée des vues `Overall Code` et `New Code` est une
recette séparée, manuelle ou non bloquante : une indisponibilité du navigateur ne
doit pas invalider un déploiement dont l'application et les API répondent.

## Validation locale du 8 septembre 2026

La solution Release, les checks API complets sur PostgreSQL 16 et Azurite, le
contrat du template partagé, le scan Semgrep versionné, les checks et le build
du frontend, ainsi que l'image Docker monolithique ont été validés localement.
Une recette Chromium à 1418 × 902 contre l'API réelle et une base jetable a
également vérifié les quatre cartes A–E, leurs preuves et l'échelle de
l'historique avec un snapshot synthétique. Cette recette ne vaut ni pilote
Azure DevOps ni validation de la production.

La correction exceptionnelle du catalogue `upcoop-dotnet/2` avec `ASP0000` et
`CA1848` rend son premier rapprochement incompatible avec l'ancien snapshot du
même profil. Aucun historique n'est recalculé : les analyses sans snapshot MQR
restent affichées avec `-`, et le premier build `master` suivant établit la
nouvelle baseline.

Trois nouvelles règles observées sur la première analyse d'`Up.France.ODI.Loyalty`
manquaient au catalogue `upcoop-dotnet/2` : `SYSLIB0051` et `SYSLIB0057`
(`vulnerability`/`high`, même famille d'obsolescence sécuritaire que
`SYSLIB0022`/`SYSLIB0041` déjà cataloguées — respectivement les constructeurs
`ISerializable` hérités de l'ère BinaryFormatter et les constructeurs
`X509Certificate`/`X509Certificate2` chargeant du contenu brut, tous deux
remplacés par des API plus sûres) et `EF9102` (`code_smell`/`low`, le diagnostic
`[Experimental]` d'EF Core — un risque de maintenabilité, pas un bug ni une
vulnérabilité). Comme pour les ajouts précédents, ces règles sont ajoutées
directement à `upcoop-dotnet/2` plutôt que de publier une version `/3` : une
extension du catalogue ne peut que faire passer une analyse de `-` à une note
réelle, jamais invalider une note déjà calculée.

Les checks Dockerfile de Checkov (`CKV_DOCKER_*`, les règles de ressource, et
`CKV2_DOCKER_*`, les règles de graphe) manquaient entièrement au catalogue
`upcoop-dotnet/2` — observé sur `Up.France.ODI.Uptimise.API`, dont le
`docker/Dockerfile` déclenche `CKV_DOCKER_2`/`CKV_DOCKER_8`. Le premier import
Checkov de ce catalogue excluait délibérément Dockerfile avec les ~14 autres
fournisseurs hors Terraform-Azure/OpenAPI ; cette hypothèse était fausse dès
qu'un dépôt construit une image Docker via `UpCoopSecurityScan@2`, ce qui
concerne potentiellement tout dépôt de la plateforme. Les 28 règles
(`checkov/dockerfile/checks/*.py` + `checkov/dockerfile/checks/graph_checks/*.yaml`)
sont importées en bloc avec la même classification uniforme
`security_hotspot`/`low` que les autres imports Checkov, Checkov ne publiant
pas de taxonomie de sévérité plus fine pour cette famille non plus.

Les règles de détection de secrets propres à Checkov (`CKV_SECRET_*`)
manquaient en bloc des deux profils MQR — observé sur
`Up.France.ODI.Uptimise.BMSC` (`CKV_SECRET_3`, `CKV_SECRET_6`). Contrairement
aux imports Checkov Terraform-Azure/OpenAPI/Dockerfile, cette famille n'a pas
de liste source complète et publiée : seule la table `SECRET_TYPE_TO_ID` de
`checkov/secrets/runner.py` (19 entrées, alignée avec l'index de politiques
publié sur checkov.io) est stable et documentée — le scanner de secrets
Checkov assigne aussi des identifiants dynamiques non documentés
(`CKV_SECRET_30/40/48/49/80/116/192/380/381`...) aux types de détecteurs
hérités de la bibliothèque `detect-secrets` qu'il enveloppe, sans ID Checkov
fixe. Seules les 19 entrées stables sont importées ; un identifiant dynamique
non encore vu continuera d'apparaître en catégorie inconnue, un compromis
similaire à celui des identifiants CVE/GHSA de Trivy. Même catégorie
`secret`/`medium` que les imports Gitleaks et Trivy secret déjà en place.
Sur `upcoop-react/2`, les deux entrées `CKV_SECRET_6`/`CKV_SECRET_9`
auparavant choisies à la main sont remplacées par cet import complet.

Trois nouveaux imports en bloc sur `upcoop-dotnet/2`, tous observés sur la
première analyse d'`Up.France.ODI.Uptimise.API` :

- **Règles de style Roslyn IDE** (`IDE0028` déclenché) : 129 entrées depuis
  l'index publié par Microsoft Learn (« Code-style rules overview »), absentes
  des fichiers `AnalyzerReleases` que les NetAnalyzers (`CA*`) publient — cette
  famille n'a qu'un tableau de documentation, pas de taxonomie de sévérité.
  Classification uniforme `code_smell`/`low`/5 min, même traitement que les
  avertissements du compilateur C#. `IDE3000` (« Implement with Copilot ») est
  exclu : un déclencheur d'action Visual Studio, pas un vrai diagnostic
  d'analyseur.
- **Règles de l'analyseur ASP.NET Core** (`ASP0025` déclenché) : 26 entrées
  depuis `DiagnosticDescriptors.cs` de `dotnet/aspnetcore`, le fichier source
  unique définissant chaque diagnostic `ASP0*` avec sa vraie catégorie Roslyn
  (`Security`/`Usage`) et sa vraie sévérité — un signal par règle réel, pas une
  supposition. Catégorie `Security` → `vulnerability` ; `Usage` à sévérité
  `Info` → `code_smell` (remplace les entrées `ASP0015`/`ASP0027` auparavant
  choisies à la main) ; `Usage` à `Warning`/`Error` → `bug`, ce paquet
  d'analyseurs existant spécifiquement pour détecter un mauvais usage de l'API,
  pas un style. `ASP0000` (absent de ce fichier source) et `ASPDEPR006` (une
  famille de diagnostics de dépréciation distincte) restent inchangés.
- **Checks de mauvaise configuration Dockerfile de Trivy** (`DS-0002`,
  `DS-0026`, `DS-0029` déclenchés) : 28 entrées depuis
  `checks/docker/*.rego` d'`aquasecurity/trivy-checks`, chacune portant sa
  vraie métadonnée `# custom: id: DS-*`/`severity:`, préservée comme pour les
  checks Terraform Azure déjà importés — distinct des checks Dockerfile de
  Checkov (`CKV_DOCKER_*`, un outil indépendant sur le même fichier) et des
  checks Terraform de Trivy (`AZU-*`, un autre mode du même scanner).

Deux nouveaux imports en bloc sur `upcoop-dotnet/2`, déclenchés par la première
analyse d'`Up.France.ODI.Services.Beneficiaire` : `EF1001` absent du catalogue
rendait toute l'analyse `technical_state: incomplete`.

- **Diagnostics EF Core** (`EF1001` déclenché) : 12 entrées. Les règles
  d'analyseur viennent d'`AnalyzerReleases.Shipped/Unshipped.md` de
  `src/EFCore.Analyzers` dans `dotnet/efcore`, avec leur vraie catégorie et
  sévérité Roslyn, et suivent le même mapping que l'import ASP.NET Core :
  `Security` → `vulnerability` (`EF1002`/`EF1003`, SQL brut construit par
  interpolation ou concaténation) ; `Usage` → `bug` (`EF1001` API interne,
  `EF1004` `ToAsyncEnumerable` sur un `IQueryable`) ; `Warning` → `medium`.
  `EF1000`, retiré par Microsoft, est exclu. Les identifiants `[Experimental]`
  de `src/Shared/EFDiagnostics.cs` (`EF9001`…`EF9106`) reprennent le traitement
  `code_smell`/`low`/10 min d'`EF9102`, qui reste choisi à la main.
- **Diagnostics `SYSLIB`** : 185 entrées depuis
  `docs/project/list-of-diagnostics.md` de `dotnet/runtime` — obsolescences
  (`SYSLIB0xxx`), analyseurs et générateurs de source (`SYSLIB1xxx`), API
  expérimentales (`SYSLIB5xxx`). Les lignes « reserved » ne sont pas des
  diagnostics et sont exclues. Microsoft ne publie pas de catégorie pour cette
  famille : classification uniforme `code_smell`/`low`/10 min, celle de
  `SYSLIB1045`. Les obsolescences de sécurité choisies à la main
  (`SYSLIB0022`/`0041`/`0051`/`0057`, `vulnerability`/`high`) restent
  prioritaires et sont exclues de l'import.
