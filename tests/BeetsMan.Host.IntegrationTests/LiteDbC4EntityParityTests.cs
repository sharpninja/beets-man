using System.Text.RegularExpressions;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Reconciles the C4 document shapes with role-specific production mapper scopes.</summary>
/// <remarks>2026-11-26: Bind TEST-RECORDS-002 to TR-RECORDS-ARCH-001#ac-litedb-c4-entities. Complements the runtime BSON and replay tests.</remarks>
public sealed class LiteDbC4EntityParityTests
{
    private const string Prefix = "LiteDbParity.";
    private static readonly Regex Entities = new("""^\s*(\w+) = element "(Desktop|Host) / ([a-z_]+)" "LiteDB collection" "([^"\r\n]+)" """,
        RegexOptions.Multiline);
    private static readonly Regex Relationships = new("""^\s*(\w+) -> (\w+) "([^"\r\n]+)"\s*$""", RegexOptions.Multiline);
    private static readonly Regex MappedFields = new("""\["(\w+)"\]\s*=(?!=|>)""");
    private static readonly Regex CollectionNames = new("""
        (?:GetCollection<BsonDocument>\(\s*|CatalogEntityKind\.\w+ => |const string \w+ = )"([a-z]+_[a-z_]+)"
        """);

    /// <summary>Checks the collection inventories, scoped BSON writes, key construction, compatibility rules, and relationship witnesses.</summary>
    [Fact]
    public void DesktopAndHostMappingsMatchDocumentedKeysFieldsAndRelationships()
    {
        var resources = typeof(LiteDbC4EntityParityTests).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix + "Desktop.", StringComparison.Ordinal) ||
                           name.StartsWith(Prefix + "Host.", StringComparison.Ordinal))
            .ToDictionary(name => name[Prefix.Length..], name => Read(name), StringComparer.Ordinal);
        var failures = Compare(Read(Prefix + "Model"), Read(Prefix + "Mappings"), Read(Prefix + "Relationships"), resources);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Proves the parity checker rejects an inventory, key, field, optional default, or edge drift under the same assertions.</summary>
    [Fact]
    public void DetectsIndependentChangesInTheModelAndMapping()
    {
        const string model = """
            desktopItem = element "Desktop / example_items" "LiteDB collection" "_id: (OwnerId, ItemId). OwnerId: Guid. Count: optional Int32, defaults to zero." "Desktop LiteDB Entity"
            desktopOwner = element "Desktop / example_owners" "LiteDB collection" "_id: OwnerId (Guid)." "Desktop LiteDB Entity"
            desktopItem -> desktopOwner "References OwnerId"
            """;
        const string repository = """
            const string Items = "example_items";
            const string Owners = "example_owners";
            WriteItem() => new(_id: key);
                ["_id"] = Key(owner, item), ["OwnerId"] = owner, ["Count"] = count,
            ReadItem() => row["Count"].IsInt32 ? row["Count"].AsInt32 : 0;
            WriteOwner() => new() { ["_id"] = owner };
            """;
        const string mappings = """
            key|desktopItem||||(OwnerId, ItemId)
            rule|desktopItem||||Count: optional Int32, defaults to zero.
            key|desktopOwner||||OwnerId (Guid)
            map|desktopItem|Repo|WriteItem()|ReadItem()|_id OwnerId Count
            code|desktopItem|Repo|WriteItem()|ReadItem()|["_id"] = Key(owner, item)
            code|desktopItem|Repo|ReadItem()|WriteOwner()|row["Count"].AsInt32 : 0
            map|desktopOwner|Repo|WriteOwner()||_id
            code|desktopOwner|Repo|WriteOwner()||["_id"] = owner
            """;
        const string edges = """desktopItem|desktopOwner|References OwnerId|Repo|WriteItem()|ReadItem()|["OwnerId"] = owner""";
        IReadOnlyList<string> Evaluate(string c4, string source) =>
            Compare(c4, mappings, edges, new Dictionary<string, string> { ["Desktop.Repo"] = source });
        Assert.Empty(Evaluate(model, repository));
        Assert.Contains(Evaluate(model, repository.Replace("example_items", "wrong_collection")), text => text.Contains("collection inventory"));
        Assert.Contains(Evaluate(model, repository.Replace("Key(owner, item)", "Key(item, owner)")), text => text.Contains("mapping expression"));
        Assert.Contains(Evaluate(model.Replace("(OwnerId, ItemId)", "(ItemId, OwnerId)"), repository), text => text.Contains("documented key"));
        Assert.Contains(Evaluate(model, repository.Replace("[\"Count\"] = count", "[\"Other\"] = count")), text => text.Contains("BSON fields"));
        Assert.Contains(Evaluate(model, repository.Replace("AsInt32 : 0", "AsInt32 : 1")), text => text.Contains("mapping expression"));
        Assert.Contains(Evaluate(model.Replace("defaults to zero", "defaults to one"), repository), text => text.Contains("documented field rule"));
        Assert.Contains(Evaluate(model.Replace("References OwnerId", "References ItemId"), repository), text => text.Contains("relationship inventory"));
        Assert.Contains(Evaluate(model, repository.Replace("[\"OwnerId\"] = owner", "[\"OwnerId\"] = item")), text => text.Contains("relationship expression"));
    }

    /// <summary>Compares the model and mapper witnesses independently and reports every mismatch.</summary>
    private static IReadOnlyList<string> Compare(string model, string mappings, string edges, IReadOnlyDictionary<string, string> repositories)
    {
        var failures = new List<string>();
        var entities = Entities.Matches(model).ToDictionary(match => match.Groups[1].Value, StringComparer.Ordinal);
        foreach (var role in new[] { "Desktop", "Host" })
        {
            var actual = repositories.Where(repo => repo.Key.StartsWith(role + ".", StringComparison.Ordinal))
                .SelectMany(repo => CollectionNames.Matches(repo.Value).Select(match => match.Groups[1].Value));
            var documented = entities.Values.Where(entity => entity.Groups[2].Value == role).Select(entity => entity.Groups[3].Value).ToArray();
            CompareSet(role + " collection inventory", documented, actual);
            if (documented.Length != documented.Distinct(StringComparer.Ordinal).Count())
                failures.Add(role + " declares a collection more than once.");
        }

        var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
        var documentedKeys = new HashSet<string>(StringComparer.Ordinal);
        var recordedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(mappings, 6))
        {
            var (kind, id) = (row[0], row[1]);
            if (!entities.TryGetValue(id, out var entity))
            {
                failures.Add(id + " mapper refers to an undeclared C4 entity.");
                continue;
            }
            var description = entity.Groups[4].Value;
            if (kind == "key")
            {
                if (!documentedKeys.Add(id) || !description.StartsWith("_id: " + row[5], StringComparison.Ordinal))
                    failures.Add(id + " documented key differs from " + row[5]);
                continue;
            }
            if (kind == "rule")
            {
                CheckExpression(id + " documented field rule", description, row[5]);
                continue;
            }
            var scope = SourceScope(id, entity.Groups[2].Value, row[2], row[3], row[4]);
            switch (kind)
            {
                case "map":
                    var expected = row[5].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    CompareSet(id + " BSON fields at " + row[3], expected, MappedFields.Matches(scope).Select(match => match.Groups[1].Value));
                    foreach (var field in expected)
                        if (!Regex.IsMatch(entity.Groups[4].Value, $@"(?<!\w){Regex.Escape(field)}(?!\w)"))
                            failures.Add($"{id} C4 description is missing {field}.");
                    recordedFields.Add(id);
                    break;
                case "code":
                    CheckExpression(id + " mapping expression", scope, row[5]);
                    if (row[5].StartsWith("[\"_id\"] =", StringComparison.Ordinal))
                        recordedKeys.Add(id);
                    break;
                default:
                    failures.Add(id + " has an invalid mapping row kind " + kind);
                    break;
            }
        }
        CompareSet("entities with documented BSON writers", entities.Keys, recordedFields);
        CompareSet("entities with pinned storage-key assignments", entities.Keys, recordedKeys);
        CompareSet("entities with pinned documented keys", entities.Keys, documentedKeys);

        var expectedEdges = new List<string>();
        foreach (var row in Rows(edges, 7))
        {
            var description = $"{row[0]} -> {row[1]} \"{row[2]}\"";
            expectedEdges.Add(description);
            if (!entities.TryGetValue(row[0], out var from) || !entities.ContainsKey(row[1]))
            {
                failures.Add(description + " has an unknown endpoint.");
                continue;
            }
            var scope = SourceScope(description, from.Groups[2].Value, row[3], row[4], row[5]);
            CheckExpression(description + " relationship expression", scope, row[6]);
        }
        CompareSet("relationship inventory", expectedEdges, Relationships.Matches(model).Select(edge => edge.Value.Trim()));
        return failures;

        void CompareSet(string label, IEnumerable<string> expected, IEnumerable<string> actual)
        {
            var added = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (added.Length + missing.Length > 0)
                failures.Add($"{label}: unexpected [{string.Join(", ", added)}], missing [{string.Join(", ", missing)}].");
        }

        string SourceScope(string label, string role, string file, string first, string after)
        {
            if (!repositories.TryGetValue(role + "." + file, out var source))
            {
                failures.Add(label + " source file is missing: " + file);
                return "";
            }
            source = Normalize(source);
            var start = source.IndexOf(first, StringComparison.Ordinal);
            var end = after.Length == 0 ? source.Length : start < 0 ? -1 : source.IndexOf(after, start + first.Length, StringComparison.Ordinal);
            if (start >= 0 && end > start)
                return source[start..end];
            failures.Add(label + " mapper anchors are missing or out of order: " + first + " / " + after);
            return "";
        }

        void CheckExpression(string label, string scope, string expected)
        {
            if (expected.Length == 0 || !scope.Contains(expected, StringComparison.Ordinal))
                failures.Add($"{label} is missing {expected}.");
        }
    }

    /// <summary>Parses the manually specified witnesses. The last column can contain C# operators.</summary>
    private static IEnumerable<string[]> Rows(string document, int fields) =>
        document.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).Select(line => line.Split('|', fields, StringSplitOptions.TrimEntries))
            .Select(row => row.Length == fields ? row : throw new InvalidDataException("Malformed LiteDB parity row: " + string.Join('|', row)));

    /// <summary>Ignores insignificant source formatting between witness anchors.</summary>
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ");

    /// <summary>Reads the actual build inputs retained in the cross-compiled test assembly.</summary>
    private static string Read(string name)
    {
        using var stream = typeof(LiteDbC4EntityParityTests).Assembly.GetManifestResourceStream(name) ??
            throw new InvalidDataException("Missing LiteDB parity resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
