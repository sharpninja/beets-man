using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Runs the named beets acceptance scenarios against registered HTTPS, local files and the native worker.</summary>
/// <remarks>2026-11-26: Bind the production TEST-BEETS-002 cases for FR-BEETS-001#ac-open, FR-BEETS-001#ac-edit, FR-BEETS-001#ac-import and FR-BEETS-001#ac-recover.</remarks>
public sealed class BEETSAcceptanceTests : IDisposable
{
    private readonly PlaylistHttpsIntegrationTests fixture = new();

    /// <summary>Opens the authorized existing library and creates another only with approved settings.</summary>
    [Fact]
    public Task Open_MatchesContract() => fixture.BeetsLibrarySetupOpensExistingAndInstallsTheApprovedNativeLibrary();

    /// <summary>Checks the tagged revision and the separate approval for organization and removal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Edit_MatchesContract(bool trackTags) => trackTags
        ? fixture.TrackTagDraftReadsTheSelectedRevisionAndRecoversOneAcceptedEdit()
        : fixture.ReadOnlyMaintenancePreviewAndExactApprovalSurviveRestart();

    /// <summary>Exercises each explicit decision in the guided import review.</summary>
    [Theory]
    [InlineData(ImportChoiceKind.UseMatch)]
    [InlineData(ImportChoiceKind.KeepTags)]
    [InlineData(ImportChoiceKind.Skip)]
    public Task Import_MatchesContract(ImportChoiceKind choice) => fixture.GuidedImportReviewJournalsExactlyOneAlbumChoice(choice);

    /// <summary>Checks that a resumed worker reconciles existing effects before issuing another native write.</summary>
    [Theory]
    [InlineData("First")]
    [InlineData("Hosted")]
    [InlineData("Recovered")]
    [InlineData("Incomplete")]
    public Task Recover_MatchesContract(string scenario) => fixture.GuidedImportWorkerReconcilesBeforeAnyRepeatWrite(scenario);

    /// <inheritdoc />
    public void Dispose() => fixture.Dispose();
}

/// <summary>Runs the named registration and replication acceptance scenarios through separate durable peers.</summary>
/// <remarks>2026-11-26: Bind TEST-RECORDS-002 cases for FR-RECORDS-001#ac-register, FR-RECORDS-001#ac-notify, FR-RECORDS-001#ac-replicate and FR-RECORDS-001#ac-offline.</remarks>
public sealed class RECORDSAcceptanceTests : IDisposable
{
    private readonly PlaylistHttpsIntegrationTests fixture = new();

    /// <summary>Authorizes registered peers and rejects the revoked peer after an exact registry change.</summary>
    [Fact]
    public Task Register_MatchesContract() => fixture.RegisteredDesktopEnrollmentAndRevocationMatchTheContract();

    /// <summary>Delivers and acknowledges changes independently across a lost acknowledgment.</summary>
    [Fact]
    public Task Notify_MatchesContract() => fixture.ReplicaCommitsFixedWindowBeforeAcknowledgingAndResumesAfterLostAck();

    /// <summary>Converges stable listening records and shared preferences through feed and snapshot.</summary>
    [Fact]
    public Task Replicate_MatchesContract() => fixture.ListeningRecordsAndSharedDefaultsConvergeThroughFeedAndSnapshot();

    /// <summary>Replays stable offline operations and retains conflicts after snapshot recovery.</summary>
    [Fact]
    public Task Offline_MatchesContract() => fixture.OfflineListenerOutboxReplaysExactOperationsAndRetainsConflictsAcrossSnapshot();

    /// <inheritdoc />
    public void Dispose() => fixture.Dispose();
}
