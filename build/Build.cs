using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>Runs isolated and integration tests and retains per-project TRX results.</summary>
/// <remarks>2026-11-24: Establish the mock and production test gates for TEST-DISPATCH-001.</remarks>
/// <remarks>2026-11-24: Accept dedicated host test groups for TEST-RECORDS-001 and TEST-RECORDS-002.</remarks>
internal sealed class Build : NukeBuild
{
    private const string TestProject = "BeetsMan.Desktop.Tests";
    private const string IntegrationProject = "BeetsMan.Desktop.IntegrationTests";
    private const string HostTestProject = "BeetsMan.Host.Tests";
    private const string HostIntegrationProject = "BeetsMan.Host.IntegrationTests";

    [Parameter("Selected projects and test classes", Name = "test-groups")]
    private readonly string[] testGroupSelection = [];

    [Parameter("Test evidence directory identifier")]
    private readonly string? testRunId = null;

    public static int Main() => Execute<Build>(build => build.Test);

    private Target Compile => _ => _.Executes(() =>
        DotNetBuild(settings => settings.SetProjectFile(RootDirectory / "BeetsMan.slnx")));

    private Target Test => _ => _.Executes(() =>
    {
        RunTests(TestProject, null, "all");
        RunTests(HostTestProject, null, "all");
    });

    private Target MigrationIntegrationTests => _ => _.Executes(() =>
    {
        RunTests(IntegrationProject, null, "integration");
        RunTests(HostIntegrationProject, null, "integration");
    });

    private Target TestGroups => _ => _.Executes(() =>
    {
        if (testGroupSelection.Length == 0)
            throw new ArgumentException($"Specify --test-groups {TestProject}:TestClass[,TestClass] [Project:TestClass].");
        foreach (var group in testGroupSelection.Select(ParseGroup).GroupBy(group => group.Project, StringComparer.Ordinal))
            RunTests(group.Key, group.SelectMany(group => group.Classes).Distinct(StringComparer.Ordinal).ToArray(), "groups");
    });

    private static (string Project, string[] Classes) ParseGroup(string group)
    {
        if (group.Split(':', 2) is not [var project, var classes] ||
            project is not (TestProject or IntegrationProject or HostTestProject or HostIntegrationProject) || string.IsNullOrWhiteSpace(classes))
            throw new ArgumentException($"Unknown project or missing test classes in '{group}'.");
        var names = classes.Split(',', StringSplitOptions.TrimEntries);
        if (names.Any(name => name.Length == 0 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("Test class names must be simple identifiers.");
        return (project, names);
    }

    private void RunTests(string testProject, string[]? classes, string lane)
    {
        var runId = testRunId ?? DateTime.Now.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        if (runId.Length == 0 || runId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Test run IDs use letters, digits, hyphens and underscores.");

        var project = RootDirectory / "tests" / testProject / $"{testProject}.csproj";
        var results = RootDirectory / "TestResults" / runId / lane / testProject;
        var filter = classes is null ? null : string.Join('|', classes.Select(name => $"FullyQualifiedName~.{name}."));
        DotNetTest(settings =>
        {
            var configured = settings.SetProjectFile(project).SetResultsDirectory(results)
                .AddLoggers($"trx;LogFileName={testProject}.trx");
            return filter is null ? configured : configured.SetFilter(filter);
        });

        var receipt = XDocument.Load(results / $"{testProject}.trx");
        var ns = receipt.Root!.Name.Namespace;
        var tests = receipt.Descendants(ns + "UnitTestResult").ToArray();
        if (tests.Length == 0 || tests.Any(test => (string?)test.Attribute("outcome") != "Passed"))
            throw new InvalidOperationException("The gate requires at least one test and zero failures or skips.");

        var executed = tests.Select(test => (string?)test.Attribute("testId")).ToHashSet();
        var testedClasses = receipt.Descendants(ns + "UnitTest")
            .Where(test => executed.Contains((string?)test.Attribute("id")))
            .Select(test => ((string?)test.Element(ns + "TestMethod")?.Attribute("className"))?.Split('.').Last())
            .ToHashSet(StringComparer.Ordinal);
        if (classes?.Any(name => !testedClasses.Contains(name)) == true)
            throw new InvalidOperationException("At least one requested test class did not execute.");
    }
}
