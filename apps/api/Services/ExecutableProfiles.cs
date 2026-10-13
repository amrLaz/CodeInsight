using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

/// <summary>Materialized functional rule lookup; ambiguous identities never resolve.</summary>
public sealed class RuleCatalogIndex
{
    private readonly Dictionary<(string Tool, string RuleId), CatalogueRule> unique = [];
    private readonly HashSet<(string Tool, string RuleId)> ambiguous = [];
    private readonly Dictionary<string, CatalogueRule[]> categories;

    /// <summary>Whether at least one functional identity has multiple definitions.</summary>
    public bool HasAmbiguities => ambiguous.Count > 0;

    internal RuleCatalogIndex(IEnumerable<CatalogueRule> rules)
    {
        foreach (var rule in rules)
        {
            var key = (rule.Tool, rule.RuleId);
            if (ambiguous.Contains(key)) continue;
            if (unique.TryAdd(key, rule)) continue;
            unique.Remove(key);
            ambiguous.Add(key);
        }
        categories = unique.Values
            .GroupBy(rule => rule.Category, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Resolves one unambiguous rule by its functional identity.</summary>
    public CatalogueRule? Resolve(string tool, string ruleId) => unique.GetValueOrDefault((tool, ruleId));

    /// <summary>Returns the unambiguous rules that contribute to a quality category.</summary>
    public IReadOnlyCollection<CatalogueRule> RulesInCategory(string category) =>
        categories.GetValueOrDefault(category) ?? [];
}

/// <summary>Versioned producer metadata and native configuration exports; never executes analyzers.</summary>
public static class ExecutableProfiles
{
    private static bool Identifier(string? value, int maximum = 200) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Https(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0;
    public static void Validate(ProfileDefinition profile)
    {
        if (profile.RuleCatalog is null || profile.AnalyzerConfigurations is null || profile.DisabledRules is null || profile.RuleCatalog.Length > 10000 || profile.AnalyzerConfigurations.Length > 100
            || profile.DisabledRules.Length > 10000)
            throw new ApiException(422, "Invalid rule catalogue or analyzer configurations");
        foreach (var rule in profile.RuleCatalog)
            if (rule is null || !Identifier(rule.Tool, 100) || !Identifier(rule.ToolVersion, 100) || !Identifier(rule.RuleId) || !Identifier(rule.Language, 100)
                || rule.Category is not ("bug" or "code_smell" or "vulnerability" or "security_hotspot" or "secret" or "unknown")
                || rule.Severity
                    is not null
                        and not ("info" or "low" or "medium" or "high" or "blocker")
                || rule.Parameters.ValueKind != JsonValueKind.Object || rule.Parameters.GetRawText().Length > 16000
                || rule.DocumentationUrl is not null && !Https(rule.DocumentationUrl)
                || rule.DocumentationVersion is not null && !Identifier(rule.DocumentationVersion)
                || rule.Remediation is { } remediation && (remediation.Model != "constant" || remediation.DurationSeconds is <= 0 or > 31536000 || !Https(remediation.DocumentationUrl)))
                throw new ApiException(422, "Rule catalogue requires producer/version, rule, language, parameters, category and safe producer documentation");
        var ruleCatalog = BuildRuleIndex(profile);
        if (ruleCatalog.HasAmbiguities)
            throw new ApiException(422, "Duplicate producer/rule identity in catalogue");
        long size = 0;
        foreach (var config in profile.AnalyzerConfigurations)
        {
            if (config is null || !Identifier(config.Tool, 100) || !Identifier(config.ToolVersion, 100) || !IngestionValidation.SafePath(config.Path)
                || config.Content is null || (size += Encoding.UTF8.GetByteCount(config.Content)) > 1024 * 1024 || config.Content.Contains('\0'))
                throw new ApiException(422, "Invalid or oversized analyzer configuration");
            ValidateNative(config);
        }
        if (profile.AnalyzerConfigurations.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.AnalyzerConfigurations.Length)
            throw new ApiException(422, "Configuration export paths must be unique on Windows and Linux");
        if (profile.DisabledRules.Any(rule => rule is null || !Identifier(rule.Tool, 100) || !Identifier(rule.ToolVersion, 100) || !Identifier(rule.RuleId))
            || profile.DisabledRules.Select(rule => (rule.Tool, rule.RuleId)).Distinct().Count() != profile.DisabledRules.Length)
            throw new ApiException(422, "Disabled rules require a unique producer and rule identity");
        foreach (var rule in profile.DisabledRules)
        {
            if (ruleCatalog.Resolve(rule.Tool, rule.RuleId) is null || !CanManage(profile, rule))
                throw new ApiException(422, "A disabled rule must exist in the catalogue and in a supported analyzer configuration");
        }
    }

    private static void ValidateNative(AnalyzerConfiguration config)
    {
        if (config.Format == "editorconfig")
        {
            if (!config.Path.EndsWith(".editorconfig", StringComparison.Ordinal) || !config.Content.Split('\n').Any(line => line.TrimStart().StartsWith('[')))
                throw new ApiException(422, "EditorConfig requires an .editorconfig path and a file section");
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(config.Content);
            var root = document.RootElement;
            if (config.Format == "semgrep-json" && config.Tool == "semgrep" && config.Path.EndsWith(".json", StringComparison.Ordinal)
                && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array
                && rules.EnumerateArray().All(rule => rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && rule.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
                    && rule.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    && rule.TryGetProperty("severity", out var severity) && severity.ValueKind == JsonValueKind.String)) return;
            // ponytail: JSON-only flat ESLint core rules; plugins/parsers require native repository config, not executable code uploaded to this service.
            if (config.Format == "eslint-flat-json" && config.Tool == "eslint" && config.Path.EndsWith(".cjs", StringComparison.Ordinal)
                && root.ValueKind == JsonValueKind.Array && root.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object
                    && item.EnumerateObject().All(property => property.Name is "name" or "files" or "ignores" or "languageOptions" or "linterOptions" or "rules" or "settings"))) return;
        }
        catch (JsonException) { }
        throw new ApiException(422, "Supported native exports: editorconfig, semgrep-json and eslint-flat-json (core rules)");
    }

    public static ConfigurationExport Export(ProfileDefinition profile)
    {
        var files = profile.AnalyzerConfigurations.Select(config =>
        {
            var disabled = profile.DisabledRules.Where(rule => rule.Tool == config.Tool).ToArray();
            var native = disabled.Length == 0 ? config.Content : Disable(config, disabled);
            var content = config.Format == "eslint-flat-json" ? "module.exports = " + native + ";\n" : native;
            return new ExportedConfiguration(config.Tool, config.ToolVersion, config.Format, config.Path, content, Hash(content));
        }).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        return new(profile.ProfileId, profile.Version, Fingerprint(files.Select(file => new EffectiveConfiguration(file.Tool, file.ToolVersion, file.Path, file.Sha256))), files);
    }

    public static void Validate(IngestionManifest manifest)
    {
        if (manifest.EffectiveConfigurations is null || manifest.EffectiveConfigurations.Length > 100 || manifest.EffectiveConfigurations.Any(file => file is null
            || !Identifier(file.Tool, 100) || !Identifier(file.ToolVersion, 100) || !IngestionValidation.SafePath(file.Path)
            || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
            || manifest.EffectiveConfigurations.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.EffectiveConfigurations.Length)
            throw new ApiException(422, "Invalid effective analyzer configuration evidence");
    }

    public static ConfigurationEvaluation Evaluate(ProfileDefinition profile, IngestionManifest manifest)
    {
        var expected = Export(profile);
        var effectiveHash = Fingerprint(manifest.EffectiveConfigurations);
        if (expected.Files.Length == 0) return new("not_configured", expected.ConfigurationHash, effectiveHash, "no_executable_configuration");
        if (manifest.EffectiveConfigurations.Length == 0) return new("missing", expected.ConfigurationHash, effectiveHash, "effective_configuration_missing");
        if (expected.Files.Any(file => !manifest.Reports.Any(report => report.Tool == file.Tool && report.State == "measured")))
            return new("missing", expected.ConfigurationHash, effectiveHash, "configured_analyzer_report_missing");
        return expected.ConfigurationHash == effectiveHash ? new("matched", expected.ConfigurationHash, effectiveHash, null)
            : new("drift", expected.ConfigurationHash, effectiveHash, "configuration_hash_drift");
    }

    public static string Fingerprint(IEnumerable<EffectiveConfiguration> configurations) => Hash(string.Concat(configurations.OrderBy(x => x.Path, StringComparer.Ordinal)
        .Select(file => $"{file.Tool}\n{file.Path}\n{file.Sha256.ToLowerInvariant()}\n")));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string RuleKey(CatalogueRule rule) => JsonSerializer.Serialize(new[] { rule.Tool, rule.RuleId });
    public static string RuleKey(DisabledRule rule) => JsonSerializer.Serialize(new[] { rule.Tool, rule.RuleId });
    public static RuleCatalogIndex BuildRuleIndex(ProfileDefinition profile) => new(profile.RuleCatalog);

    public static bool CanManage(ProfileDefinition profile, DisabledRule rule)
    {
        foreach (var config in profile.AnalyzerConfigurations.Where(config => config.Tool == rule.Tool))
        {
            if (config.Format == "editorconfig" && SafeEditorConfigRule(rule.RuleId)) return true;
            var root = JsonNode.Parse(config.Content);
            if (config.Format == "eslint-flat-json" && root is JsonArray eslint && eslint.OfType<JsonObject>()
                    .Any(item => item["rules"] is JsonObject rules && rules.ContainsKey(rule.RuleId))) return true;
            if (config.Format == "semgrep-json" && root is JsonObject semgrep && semgrep["rules"] is JsonArray rulesArray
                && rulesArray.OfType<JsonObject>().Any(item => item["id"]?.GetValue<string>() == rule.RuleId)) return true;
        }
        return false;
    }

    public static bool Matches(CatalogueRule catalogue, DisabledRule disabled) => catalogue.Tool == disabled.Tool && catalogue.RuleId == disabled.RuleId;
    public static bool Matches(DisabledRule left, DisabledRule right) => left.Tool == right.Tool && left.RuleId == right.RuleId;

    private static bool SafeEditorConfigRule(string value) => value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string Disable(AnalyzerConfiguration config, DisabledRule[] disabled)
    {
        if (config.Format == "editorconfig")
        {
            var suffix = new StringBuilder(config.Content.TrimEnd()).AppendLine().AppendLine()
                .AppendLine("# CodeInsights disabled rules").AppendLine("[*.{cs,vb}]");
            foreach (var rule in disabled.OrderBy(rule => rule.RuleId, StringComparer.Ordinal))
                suffix.Append("dotnet_diagnostic.").Append(rule.RuleId).AppendLine(".severity = none");
            return suffix.ToString();
        }
        var root = JsonNode.Parse(config.Content) ?? throw new ApiException(422, "Invalid analyzer configuration");
        if (config.Format == "eslint-flat-json" && root is JsonArray eslint)
        {
            foreach (var item in eslint.OfType<JsonObject>())
                if (item["rules"] is JsonObject rules)
                    foreach (var rule in disabled.Where(rule => rules.ContainsKey(rule.RuleId))) rules[rule.RuleId] = "off";
            return root.ToJsonString();
        }
        if (config.Format == "semgrep-json" && root is JsonObject semgrep && semgrep["rules"] is JsonArray semgrepRules)
        {
            var identities = disabled.Select(rule => rule.RuleId).ToHashSet(StringComparer.Ordinal);
            for (var index = semgrepRules.Count - 1; index >= 0; index--)
                if (semgrepRules[index] is JsonObject item && item["id"] is JsonValue id && identities.Contains(id.GetValue<string>())) semgrepRules.RemoveAt(index);
            return root.ToJsonString();
        }
        throw new ApiException(422, "Rule activation is supported only for editorconfig, semgrep-json and eslint-flat-json configurations");
    }

    public static object EstimateRemediation(ProfileDefinition profile, RuleCatalogIndex ruleCatalog, IEnumerable<FindingOccurrence> findings, IngestionManifest manifest)
    {
        long seconds = 0; var modeled = 0; var unmodeled = 0;
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            if (ruleCatalog.Resolve(finding.Tool, finding.RuleId)?.Remediation is { } model)
            { seconds += model.DurationSeconds; modeled++; sources.Add(model.DocumentationUrl); }
            else unmodeled++;
        }
        return new { state = modeled == 0 ? "not_measured" : unmodeled > 0 ? "partial" : "estimated", estimated_seconds = modeled == 0 ? (long?)null : seconds,
            modeled_findings = modeled, unmodeled_findings = unmodeled, model = "producer_constant_seconds/v1", profile_id = profile.ProfileId, profile_version = profile.Version,
            documentation_sources = sources.OrderBy(source => source, StringComparer.Ordinal).ToArray(), scope = "global" ,
        };
    }

    public static object Diff(ProfileDefinition before, ProfileDefinition after)
    {
        var left = before.RuleCatalog.GroupBy(RuleKey).ToDictionary(group => group.Key, group => group.OrderBy(rule => rule.ToolVersion, StringComparer.Ordinal).ToArray());
        var right = after.RuleCatalog.GroupBy(RuleKey).ToDictionary(group => group.Key, group => group.OrderBy(rule => rule.ToolVersion, StringComparer.Ordinal).ToArray());
        return new { from_version = before.Version, to_version = after.Version, added_rules = right.Keys.Except(left.Keys).ToArray(), removed_rules = left.Keys.Except(right.Keys).ToArray(),
            changed_rules = right.Keys.Intersect(left.Keys).Where(key => JsonSerializer.Serialize(left[key], ApiJson.Options) != JsonSerializer.Serialize(right[key], ApiJson.Options)).ToArray(),
            enabled_rules = before.DisabledRules.Select(RuleKey).Except(after.DisabledRules.Select(RuleKey)).ToArray(),
            disabled_rules = after.DisabledRules.Select(RuleKey).Except(before.DisabledRules.Select(RuleKey)).ToArray(),
            before_configuration_hash = Export(before).ConfigurationHash, after_configuration_hash = Export(after).ConfigurationHash,
            before_conditions = before.Conditions, after_conditions = after.Conditions, before_required_tools = before.RequiredTools, after_required_tools = after.RequiredTools ,
        };
    }
}
