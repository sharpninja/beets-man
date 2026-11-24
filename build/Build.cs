using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>Runs the isolated desktop tests and retains per-run TRX results.</summary>
/// <remarks>2026-11-24: Establish the mock and production test gates for TEST-DISPATCH-001.</remarks>
internal sealed class Build : NukeBuild
{
    private const string TestProject = "BeetsMan.Desktop.Tests";

    [Parameter("Selected project and test classes", Name = "test-groups")]
    private readonly string? testGroupSelection = null;

    [Parameter("Test evidence directory identifier")]
    private readonly string? testRunId = null;

    public static int Main() => Execute<Build>(build => build.Test);

    private Target Compile => _ => _.Executes(() =>
        DotNetBuild(settings => settings.SetProjectFile(RootDirectory / "BeetsMan.slnx")));

    private Target Test => _ => _.Executes(() => RunTests(null, "all"));

    private Target TestGroups => _ => _.Executes(() =>
    {
        var selection = testGroupSelection?.Split(':', 2);
        if (selection is not [TestProject, var classes] || string.IsNullOrWhiteSpace(classes))
            throw new ArgumentException($"Specify --test-groups {TestProject}:TestClass[,TestClass].");

        var names = classes.Split(',', StringSplitOptions.TrimEntries);
        if (names.Any(name => name.Length == 0 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("Test class names must be simple identifiers.");

        RunTests(names, "groups");
    });

    private void RunTests(string[]? classes, string lane)
    {
        var runId = testRunId ?? DateTime.Now.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        if (runId.Length == 0 || runId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Test run IDs use letters, digits, hyphens and underscores.");

        var project = RootDirectory / "tests" / TestProject / $"{TestProject}.csproj";
        var results = RootDirectory / "TestResults" / runId / lane / TestProject;
        var filter = classes is null ? null : string.Join('|', classes.Select(name => $"FullyQualifiedName~.{name}."));
        DotNetTest(settings =>
        {
            var configured = settings.SetProjectFile(project).SetResultsDirectory(results)
                .AddLoggers($"trx;LogFileName={TestProject}.trx");
            return filter is null ? configured : configured.SetFilter(filter);
        });

        var receipt = XDocument.Load(results / $"{TestProject}.trx");
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
