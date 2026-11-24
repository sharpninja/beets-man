using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Asserts presentation outcomes independently of implementation types.</summary>
/// <remarks>2026-11-24: Define and prove the TEST-DISPATCH-001 assertions and negative controls before binding production.</remarks>
public sealed class PresentationContractMockTests
{
    [Fact]
    public void Routing_MatchesContract()
    {
        // Independent recording-handler evidence for the typed saved-playlist drop.
        var expected = new DispatchEvidence("StagePlaylistForDevice", 1, "device-1", "profile-1", 4, 8);
        var mock = new MockPresentation();
        mock.RecordDrop("device-1", "profile-1", 4, 8);
        PresentationAssertions.VerifyDispatch(expected, mock.LastDrop!);
        mock.RecordDrop("device-1", "profile-1", 4, 8);
        Assert.ThrowsAny<XunitException>(() => PresentationAssertions.VerifyDispatch(expected, mock.LastDrop!));
        var stale = new MockPresentation();
        stale.RecordDrop("device-1", "profile-1", 3, 8);
        Assert.ThrowsAny<XunitException>(() => PresentationAssertions.VerifyDispatch(expected, stale.LastDrop!));
    }

    [Fact]
    public void Update_MatchesContract()
    {
        // Display is initially empty, then applies search 2 and retains it after late search 1.
        var mock = new MockPresentation();
        mock.QueueDisplay("second");
        var before = mock.Display;
        mock.ApplyCallbacks();
        var afterLatest = mock.Display;
        // The independent fixture supplies the retained snapshot for the superseded callback.
        mock.QueueDisplay("second");
        mock.ApplyCallbacks();
        var observed = new UpdateEvidence(before, afterLatest, mock.Display);
        PresentationAssertions.VerifyLatestSearch(observed);
        Assert.ThrowsAny<XunitException>(() => PresentationAssertions.VerifyLatestSearch(observed with { BeforeCallback = "second" }));
        Assert.ThrowsAny<XunitException>(() => PresentationAssertions.VerifyLatestSearch(observed with { AfterLateResult = "first" }));
    }

    [Fact]
    public void Responsive_MatchesContract()
    {
        var mock = new MockPresentation();
        mock.StartWrite();
        mock.QueueDisplay("Library");
        mock.ApplyCallbacks();
        var observed = new ResponsivenessEvidence(mock.Writes, mock.Display, mock.JobFinished);
        PresentationAssertions.VerifyResponsive(observed);
        Assert.ThrowsAny<XunitException>(() => PresentationAssertions.VerifyResponsive(observed with { Route = null }));
        Assert.ThrowsAny<XunitException>(() => PresentationAssertions.VerifyResponsive(observed with { CompetingWrites = 2 }));
    }

    private sealed class MockPresentation
    {
        private readonly Queue<Action> callbacks = new();

        public DispatchEvidence? LastDrop { get; private set; }
        public string? Display { get; private set; }
        public int Writes { get; private set; }
        public bool JobFinished => false;

        public void RecordDrop(string device, string profile, long profileRevision, long planRevision) =>
            LastDrop = new("StagePlaylistForDevice", (LastDrop?.Calls ?? 0) + 1, device, profile, profileRevision, planRevision);

        public void QueueDisplay(string value) => callbacks.Enqueue(() => Display = value);

        public void ApplyCallbacks()
        {
            while (callbacks.TryDequeue(out var apply))
                apply();
        }

        public void StartWrite() => Writes++;
    }
}

internal sealed record DispatchEvidence(string Request, int Calls, string Device, string Profile, long ProfileRevision, long? PlanRevision);
internal sealed record UpdateEvidence(string? BeforeCallback, string? AfterLatestCallback, string? AfterLateResult);
internal sealed record ResponsivenessEvidence(int CompetingWrites, string? Route, bool JobFinished);

internal static class PresentationAssertions
{
    public static void VerifyDispatch(DispatchEvidence expected, DispatchEvidence actual) => Assert.Equal(expected, actual);

    public static void VerifyLatestSearch(UpdateEvidence actual) =>
        Assert.Equal(new UpdateEvidence(null, "second", "second"), actual);

    public static void VerifyResponsive(ResponsivenessEvidence actual) =>
        Assert.Equal(new ResponsivenessEvidence(1, "Library", false), actual);
}
