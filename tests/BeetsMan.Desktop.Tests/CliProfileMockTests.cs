using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves CLI profile expectations through a scripted CQRS handler.</summary>
/// <remarks>2026-11-25: Exercise TEST-CLI-001 and FR-CLI-001#ac-profiles before binding the profile owner.</remarks>
public sealed class CliProfileMockTests
{
    [Fact]
    public async Task ValidatesBothInvocationModesAndRejectsMalformedClaims()
    {
        var oneShot = Profile(CliInvocationMode.OneShot);
        var session = Profile(CliInvocationMode.Session);
        var invalid = Profile(CliInvocationMode.OneShot);
        var reports = new Dictionary<Guid, CliProfileValidation>
        {
            [oneShot.ProfileId] = new(oneShot.ProfileId, oneShot.Revision, PlaybackCapabilities.Transport, []),
            [session.ProfileId] = new(session.ProfileId, session.Revision, PlaybackCapabilities.Transport | PlaybackCapabilities.Seek, []),
            [invalid.ProfileId] = new(invalid.ProfileId, invalid.Revision, PlaybackCapabilities.None,
                [new("cli-transport-mappings-required"), new("invalid-cli-status-map")])
        };
        var router = new RequestDispatcher.Builder().RegisterQuery<ValidateCliProfile, CliProfileValidation>(new ScriptedValidation(reports)).Build();
        var request = new RequestContext(Guid.NewGuid(), null);
        var token = TestContext.Current.CancellationToken;
        var first = Assert.IsType<Outcome<CliProfileValidation>.Success>(await router.QueryAsync(new ValidateCliProfile(oneShot), request, token)).Value;
        var second = Assert.IsType<Outcome<CliProfileValidation>.Success>(await router.QueryAsync(new ValidateCliProfile(session), request, token)).Value;
        var failure = Assert.IsType<Outcome<CliProfileValidation>.Success>(await router.QueryAsync(new ValidateCliProfile(invalid), request, token)).Value;
        CliProfileAssertions.Verify(first, oneShot, PlaybackCapabilities.Transport);
        CliProfileAssertions.Verify(second, session, PlaybackCapabilities.Transport | PlaybackCapabilities.Seek);
        CliProfileAssertions.Verify(failure, invalid, PlaybackCapabilities.None, "cli-transport-mappings-required", "invalid-cli-status-map");
        Assert.ThrowsAny<XunitException>(() => CliProfileAssertions.Verify(first with { Capabilities = first.Capabilities | PlaybackCapabilities.Equalizer },
            oneShot, PlaybackCapabilities.Transport));
        Assert.ThrowsAny<XunitException>(() => CliProfileAssertions.Verify(second, oneShot, PlaybackCapabilities.Transport));
        Assert.ThrowsAny<XunitException>(() => CliProfileAssertions.Verify(second with { DraftRevision = 0 }, session,
            PlaybackCapabilities.Transport | PlaybackCapabilities.Seek));
        Assert.ThrowsAny<XunitException>(() => CliProfileAssertions.Verify(failure with { Issues = [] }, invalid,
            PlaybackCapabilities.None, "cli-transport-mappings-required", "invalid-cli-status-map"));
    }

    [Fact]
    public void SavedRevisionPreservesBoundariesAndOriginalTokens()
    {
        var stored = Profile(CliInvocationMode.Session);
        string[] expected = ["--session", "path with spaces", "--literal=$value"];
        var receipt = new CliProfileReceipt(stored.ProfileId, stored.Revision);
        CliProfileAssertions.VerifyReadback(stored, receipt, stored.ProfileId, 4, expected);
        Assert.ThrowsAny<XunitException>(() => CliProfileAssertions.VerifyReadback(stored, receipt with { Revision = 3 }, stored.ProfileId, 4, expected));
        Assert.ThrowsAny<XunitException>(() => CliProfileAssertions.VerifyReadback(stored with { Arguments = ["--session path with spaces", "--literal=$value"] },
            receipt, stored.ProfileId, 4, expected));
    }

    private static CliProfileDraft Profile(CliInvocationMode mode) =>
        new(Guid.NewGuid(), 4, 1, mode, "standalone-player", ["--session", "path with spaces", "--literal=$value"],
            PlaybackCapabilities.Transport | PlaybackCapabilities.Seek | PlaybackCapabilities.Equalizer, TimeSpan.FromSeconds(2));

    private sealed class ScriptedValidation(IReadOnlyDictionary<Guid, CliProfileValidation> reports) : IQueryHandler<ValidateCliProfile, CliProfileValidation>
    {
        public Task<Outcome<CliProfileValidation>> HandleAsync(ValidateCliProfile request, RequestContext context, CancellationToken token) =>
            Task.FromResult<Outcome<CliProfileValidation>>(new Outcome<CliProfileValidation>.Success(reports[request.ProfileDraft.ProfileId]));
    }
}
