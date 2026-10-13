using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;

public sealed class IngestionComparisonTests
{
    [Fact]
    public void All() => Run();

    private static void Run()
    {
        var diff = new GitDiff
        {
            State = "available",
            Files = [new("new.cs", "old.cs", [20, 21], new() { ["10"] = 15 })],
        };
        FindingOccurrence Finding(
            long id,
            string path,
            int? line,
            string? fingerprint = null,
            string message = "message"
        ) =>
            new()
            {
                Id = id,
                Tool = "scanner",
                RuleId = "R1",
                Path = path,
                Line = line,
                Fingerprint = fingerprint,
                Message = message,
            };
        JsonDocument Compare(
            List<FindingOccurrence> old,
            List<FindingOccurrence> current,
            Dictionary<(string Path, int Line), bool>? lines = null
        ) =>
            JsonDocument.Parse(
                IngestionProcessor.Compare(diff, old, current, lines ?? [], "baseline")
            );
        using (var moved = Compare([Finding(1, "old.cs", 10)], [Finding(0, "new.cs", 15)]))
            Counts(moved, 0, 1, 0, 0, "rename and unchanged line mapping");
        using (
            var fingerprint = Compare(
                [Finding(1, "old.cs", 1, "stable")],
                [Finding(0, "new.cs", 20, "stable")]
            )
        )
            Counts(fingerprint, 0, 1, 0, 0, "stable identity across moved lines");
        using (
            var added = Compare(
                [Finding(1, "old.cs", 10, "old")],
                [Finding(0, "new.cs", 20, "new")],
                new()
                {
                    [("new.cs", 20)] = true,
                    [("new.cs", 21)] = false,
                    [("new.cs", 15)] = true,
                }
            )
        )
        {
            Counts(added, 1, 0, 1, 0, "distinct identity in new code");
            Assert.True(
                added.RootElement.GetProperty("coverage").GetDouble() == 50,
                "coverage includes only changed executable lines"
            );
        }
        using (var ambiguous = Compare([Finding(1, "old.cs", 10)], [Finding(0, "new.cs", 20)]))
            Counts(
                ambiguous,
                0,
                0,
                0,
                1,
                "possible moved finding is not new or resolved without evidence"
            );
        using (
            var absentLocation = Compare([Finding(1, "old.cs", 10)], [Finding(0, "new.cs", null)])
        )
            Counts(
                absentLocation,
                0,
                0,
                0,
                1,
                "unknown current line preserves possible prior occurrence"
            );
        using (
            var collision = Compare(
                [Finding(1, "old.cs", 1, "shared"), Finding(2, "old.cs", 2, "shared")],
                [Finding(0, "new.cs", 20, "shared")]
            )
        )
            Counts(collision, 0, 0, 0, 1, "fingerprint collisions remain indeterminate");
        using (
            var locatedCollision = Compare(
                [Finding(1, "old.cs", 10, "shared"), Finding(2, "old.cs", 2, "shared")],
                [Finding(0, "new.cs", 15, "shared")]
            )
        )
            Counts(
                locatedCollision,
                0,
                1,
                1,
                0,
                "path and mapped line disambiguate a shared fingerprint"
            );
        using (
            var duplicateCollision = Compare(
                [Finding(1, "old.cs", 10, "shared"), Finding(2, "old.cs", 10, "shared")],
                [Finding(0, "new.cs", 15, "shared"), Finding(0, "new.cs", 15, "shared")]
            )
        )
            Counts(
                duplicateCollision,
                0,
                2,
                0,
                0,
                "identical fingerprint multiplicity is matched one occurrence at a time"
            );
        var excluded = Finding(1, "old.cs", 10);
        excluded.Excepted = true;
        using (var exception = Compare([excluded], []))
            Counts(exception, 0, 0, 0, 0, "excluded occurrences not compared");
        using (var empty = Compare([], []))
            Assert.True(
                empty.RootElement.GetProperty("coverage").ValueKind == JsonValueKind.Null,
                "unmeasured new coverage remains null"
            );
        Console.WriteLine("Ingestion Git comparison checks passed.");
    }

    private static void Counts(
        JsonDocument value,
        int added,
        int persistent,
        int resolved,
        int indeterminate,
        string name
    )
    {
        var root = value.RootElement;
        Assert.True(
            root.GetProperty("new_count").GetInt32() == added
                && root.GetProperty("persistent_count").GetInt32() == persistent
                && root.GetProperty("resolved_count").GetInt32() == resolved
                && root.GetProperty("indeterminate_count").GetInt32() == indeterminate,
            name
        );
    }

}
