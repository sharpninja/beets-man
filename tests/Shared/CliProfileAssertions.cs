using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the capability projection and precise validation failures for a captured CLI draft.</summary>
/// <remarks>2026-11-25: Cover TEST-CLI-001 and FR-CLI-001#ac-profiles independently of process creation.</remarks>
internal static class CliProfileAssertions
{
    internal static void Verify(CliProfileValidation result, CliProfileDraft input, PlaybackCapabilities capabilities, params string[] issues)
    {
        Assert.Equal(input.ProfileId, result.ProfileId);
        Assert.Equal(input.Revision, result.DraftRevision);
        Assert.Equal(capabilities, result.Capabilities);
        Assert.Equal(issues.Order(StringComparer.Ordinal), result.Issues.Select(issue => issue.Code).Order(StringComparer.Ordinal));
    }

    internal static void VerifyReadback(CliProfileDraft profile, CliProfileReceipt receipt, Guid origin, long revision, string[] tokens)
    {
        Assert.Equal(origin, receipt.ProfileId);
        Assert.Equal(origin, profile.ProfileId);
        Assert.Equal(revision, receipt.Revision);
        Assert.Equal(revision, profile.Revision);
        Assert.Equal(tokens, profile.Arguments.ToArray());
    }
}
