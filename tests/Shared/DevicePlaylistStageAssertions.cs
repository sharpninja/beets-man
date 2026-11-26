using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Captures the saved plan and playback-order evidence for the test fixtures.</summary>
internal sealed record StageObservation(Guid PlanId, long Revision, string[] OrderedReferences, string[] StagedAudio, string PlaylistContent);

/// <summary>Observes playlist drops through the registered desktop workspace and configured host.</summary>
/// <remarks>2026-11-25: Verify FR-FILESYNC-001#ac-layouts and ac-playlist-drop across two playlist formats.</remarks>
internal sealed record ComposedDropObservation(string[] ProfileFormats, string[] AudioNames, string[] TrackOrder,
    string M3u8, string Pls, string[] FullyPresentEntries, bool RepeatKeepsEntryIds, bool DeviceUnchanged, bool RejectedDuplicateLayout,
    bool DropDisabledWithoutPeer, bool SelectedPeerLoadedActiveProfile, bool ExplicitProfileWasScoped, bool PreviewKeptStagedPlan,
    bool LeavingPeerClearedSelection, bool ReturningPeerRestoredDefaultPlan, bool OwnersStopped);

/// <summary>Observes the authenticated Android monitor as part of the desktop workspace lifetime.</summary>
/// <remarks>2026-11-25: Verify automatic arrival, scope and shutdown for FR-FILESYNC-001#ac-arrival.</remarks>
internal sealed record ComposedAndroidArrivalObservation(bool BaselineSilent, bool ReusedPresentation, int MaximumPollsInFlight,
    bool SelectedLivePhone, bool DraftPreserved, bool PlaybackPreserved, bool StopCancelledLongPoll, bool DeactivatedViewModels);

/// <summary>Captures device staging access checks and operation identity at the transport boundary.</summary>
internal sealed record StageTransportObservation(int MissingCredentialStatus, int WrongLibraryStatus, Guid OperationId,
    Guid DeviceId, string ProfileId, long ProfileRevision, long PlanRevision, int ChangedIntentStatus);

/// <summary>Captures accepted and rejected receiver-local index reads for TEST-FILESYNC-001/002.</summary>
internal sealed record ReceiverInventoryObservation(string[] Present, string[] Missing, string WrongDeviceCode, string ChangedIndexCode,
    string UnsafeEntryCode, string HiddenDestinationCode, string UnexpectedNotFoundCode);

/// <summary>Captures built-in layouts and an alternate output format for TEST-FILESYNC-001.</summary>
internal sealed record AndroidLayoutObservation(string ProfileId, long Revision, string Root, string AudioPath, string PlaylistPath,
    string AlternatePlaylistPath, string AlternateContent, string UnsafePathCode, string DuplicateKeyCode, string UnsupportedFormatCode);

/// <summary>Captures scoped device and catalog resolution and its gate before commit.</summary>
internal sealed record RegisteredLayoutObservation(string ShareId, string Format, string PlaylistPath,
    string[] OrderedPaths, string[] QueuedAudio, string WrongLibraryCode, string StaleProfileCode, string ChangedRegistrationCode);

/// <summary>Captures the authorized reload of the selected profile and its persisted plan.</summary>
internal sealed record DeviceExportReadObservation(Guid? PlanId, long? PlanRevision, Guid[] OrderedEntryIds,
    string[] Profiles, bool AlternatePlanEmpty, string WrongDeviceCode, string WrongProfileCode, string RevokedOriginCode);

/// <summary>Observes export changes, ownership conflicts and the exact removal approval.</summary>
internal sealed record ExportPreviewObservation(string[] Added, string[] Moved, string[] Removed, string[] ApprovedPaths,
    string[] ValidationCodes, bool MatchingApprovalAccepted, bool StaleApprovalRejected, bool ForeignFileRemainsUnowned);

/// <summary>Captures the physical share entries needed to prove the publication preview.</summary>
internal sealed record ExportShareReadObservation(string OwnedPath, string OwnedDigest, string ObstructingPath, string ObstructingKind,
    bool MissingPathAbsent, bool OtherFileAbsent, string WrongScopeCode);

/// <summary>Observes the durable reservation, interrupted archive state, and final publication receipt.</summary>
internal sealed record ExportJournalObservation(Guid ReservedId, Guid ReopenedId, int ReopenedNextStep, long BeforeManifestRevision,
    long FinalManifestRevision, string[] OwnedPaths, Guid ReplayedReceipt, string CompetingCode, string IncompleteCode, string ChangedIntentCode);

/// <summary>Observes verified export file work and protection of outside content.</summary>
internal sealed record ExportFileEffectsObservation(string Added, string Replaced, string Moved, bool OldMoveRemoved, bool RetiredRemoved,
    string Source, string OtherShare, string UnexpectedConflictCode, bool Replayed, string[] RetainedOriginals);

/// <summary>Observes a prepared playlist publication and verifies its content and revision binding.</summary>
internal sealed record StagedPlaylistPublishObservation(long PreviewRevision, string[] ChangedPaths, bool ShareEmptyAtPreview,
    string PublishedAudio, string PublishedPlaylist, string SourceAfterPreview, long ManifestRevision, bool ReceiptReplayed, string StalePreviewCode);

/// <summary>Observes the authorization, preview and receipt through the desktop's HTTPS command boundary.</summary>
/// <remarks>2026-11-24: Verify reviewed revision and replay for TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety.</remarks>
internal sealed record DeviceExportCommandObservation(int MissingCredentialStatus, int WrongLibraryStatus, int WrongOriginStatus,
    string WrongScopeCode, long PreviewRevision, string[] Changes, bool ShareEmptyAtPreview, string StalePublishCode,
    bool PreviewResponseLost, bool PublishResponseLost, Guid PublicationId, Guid? TransferId, bool ReceiptReplayed, long ManifestRevision);

/// <summary>Observes the exact effects of a scoped layout correction and its removal approval.</summary>
/// <remarks>2026-11-24: Verify the STB20 correction, cancellation and approval states for TEST-FILESYNC-001/002.</remarks>
internal sealed record LayoutCorrectionObservation(string CollisionPath, string BlockedCode, long ReviewedRevision,
    string[] OrderedPaths, string[] Changes, string[] RemovalPaths, bool OldExportRetainedUntilApproval, string StaleApprovalCode,
    string Playlist, string MovedAudio, string AddedAudio, bool OldPathsGone, string ForeignFile, string Source, string[] ReloadedPaths);

/// <summary>Observes committed scan scheduling, failure recovery and exact sender scope.</summary>
/// <remarks>2026-11-24: Verify scan scheduling for TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety.</remarks>
internal sealed record ExportScanObservation(bool QueueEmptyBeforeCommit, Guid PublicationId, Guid? TransferId, string InitialState,
    string FirstFailureCode, int AttemptsAfterRestart, string FinalState, string[] RequestedFolders, bool WrongSenderBlocked,
    bool SharedWithAnotherPeerBlocked, bool ReplayedReceipt, bool NoFurtherDispatch);

/// <summary>Observes path-level receiver progress, wrong versions, and required removals.</summary>
/// <remarks>2026-11-24: Verify the sender/receiver boundary for TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety.</remarks>
internal sealed record ExportArrivalObservation(string[] ReviewedPaths, long FirstBytes, long TotalBytes, bool WrongVersionIsPending,
    bool BytesWithoutRemovalIsPending, bool UnstableIndexIsPending, bool CompletedAfterMatchingTombstone, long FinalBytes,
    bool FinalReceiptSurvivesRestart, bool SourceUnchanged, bool ChangedSenderBlocked);

/// <summary>Observes the authenticated desktop transfer projection and idempotent retry.</summary>
/// <remarks>2026-11-24: Exercise FR-FILESYNC-001#ac-safety via the existing CQRS transfer commands.</remarks>
internal sealed record ExportTransferTransportObservation(Guid TransferId, Guid ShareId, int UnauthorizedStatus, int OtherLibraryStatus,
    string FailedPhase, bool CanRetry, string FailureCode, string StaleCode, bool LostRetryResponse, bool RetryReceiptReplayed,
    long RevisionIncrease, string PartialPhase, long PartialBytes, string CompletePhase, long CompletedBytes, bool RetryDisabledAfterCompletion,
    bool SameFilePublication);

/// <summary>Observes the host lifecycle and end-to-end configured export isolation.</summary>
/// <remarks>2026-11-24: Validate the shared host composition for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
internal sealed record HostStartupObservation(bool Started, string[] AvailableProfiles, string FirstShareFile, string SecondShareFile,
    bool FirstShareIsolated, bool SecondShareIsolated, string[] ScannedFolders, bool TransfersCompleted,
    int UnauthorizedStatus, bool StoreReopened, bool OverlapRejected, bool IdleBeforePublication, bool PublicationTrackedReceiver,
    bool TerminalTransferStoppedPolling, bool PeerChangeClearedTransfer, bool ShutdownCancelledTransferRead, bool VerifiedCompletionRaisedAlert);

/// <summary>Observes standalone launcher configuration and OS credential roles.</summary>
/// <remarks>2026-11-24: Verify host launch isolation for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
internal sealed record HostLaunchObservation(bool StartedOnHttps, int CorrectClientStatus, int UnknownClientStatus, int RevokedClientStatus,
    string[] CredentialReferences, bool SenderResolved, bool ReceiverResolved, bool OtherLibraryDenied, bool MissingClientCredentialRejected,
    bool ConfigurationStayedSecretFree);

/// <summary>Observes the STB10 staged list and confirms its edits stay outside the active shares.</summary>
/// <remarks>2026-11-24: Verify direct selection, shared dependencies, and review freshness for FR-FILESYNC-001#ac-safety.</remarks>
internal sealed record SyncListEditObservation(string[] AfterTrackDrop, string[] AfterFirstRemoval, string[] AfterLastRemoval,
    string[] Reloaded, string[] PreviewFiles, bool StableEntryIds, bool LostResponseReplayed, string StaleCode, string ChangedIntentCode,
    int ForeignLibraryStatus, bool OldPreviewRejected, bool MediaUnchanged);

/// <summary>Observes the STB20 cross-profile review, confirmation, and durable assignment.</summary>
/// <remarks>2026-11-24: Verify FR-FILESYNC-001#ac-layouts and ac-safety against a real profile change.</remarks>
internal sealed record ProfileChangeObservation(string[] ActiveProfiles, string ReviewedProfile, string BlockedPath, string[] RemovalPaths,
    string[] PlaylistPaths, bool UnapprovedFilesUnchanged, string ConflictingTargetCode, bool ReplayedPreview, bool InterruptedOnOldProfile,
    string[] PublishedPaths, bool RetainedForeignAndSource, bool EntryIdsPreserved);

/// <summary>Observes PC mirror progress and scoped native commands for FR-FILESYNC-001#ac-mirror.</summary>
internal sealed record PcMirrorObservation(string[] Phases, long?[] Synopsis, bool Connected, bool PausedProgressPreserved,
    string[] Mutations, bool AnotherMirrorUntouched, string WrongIdentityCode, string WrongScopeCode, string AwaitingAcceptancePhase);

/// <summary>Observes the authorized STB10 commands and persisted PC mirror projection.</summary>
internal sealed record PcMirrorCommandObservation(string[] Phases, long?[] Files, string[] NativeCalls, string StaleCode,
    string ChangedIntentCode, bool LostPauseResponseReplayed, bool PausedProjectionSurvivedRestart, bool OtherShareUnchanged,
    int ForeignLibraryStatus, bool RevokedCallerRejected, bool UnconfiguredPeerHidden);

/// <summary>Observes the STB21 live Android arrival boundary and preserved shell state.</summary>
internal sealed record AndroidArrivalObservation(bool HistoricalIgnored, bool DuplicateIgnored, bool OtherPeersIgnored,
    bool GapReconciledWithoutNavigation, bool FreshAfterGapSelected, string SelectedMode, Guid SelectedPeer,
    bool OverlayAndFocusPreserved, int ForeignLibraryStatus, bool DesktopReconnectSilent);

/// <summary>Observes both STB10 sender offers and native scope rejection before effects.</summary>
internal sealed record MediaPairingObservation(string[] SenderWrites, bool ReceiverWroteNothing, bool AndroidAwaitingAcceptance,
    bool PcAlreadyAccepted, bool KeptOtherPcMembership, bool RetryVerifiedWithoutWrites, string WrongRecipientCode,
    string ConflictingFolderCode, bool SourceUnchanged);

/// <summary>Records the results of submitting and recovering registered STB10 pairing proposals.</summary>
/// <remarks>2026-11-24: Verify scoped consent and profile assignment for FR-FILESYNC-001#ac-mirror, ac-layouts, and ac-safety.</remarks>
internal sealed record PairingCommandObservation(int MissingCredential, int WrongLibrary, int WrongOrigin, int WrongRoot,
    int WrongRole, int WrongProfile, int StaleRevision, int ChangedIntent, string[] ObservedOffers,
    string AndroidPhase, string PcPhase, string[] InitialLayouts, string ImplicitPlanProfile,
    bool ReplayedOriginalResponse, bool ReplayedWithoutNativeWork, bool UnrelatedSharePreserved);

/// <summary>Records how the paired Android folder governs status, staging, and transfer verification.</summary>
/// <remarks>2026-11-24: Exercise receiver consent and safety for FR-FILESYNC-001#ac-layouts, ac-playlist-drop, and ac-safety.</remarks>
internal sealed record AndroidAcceptanceObservation(string[] Phases, string[] RejectedFolders, bool NoReceiverWrites,
    int AwaitingInventoryReads, int WrongScopeInventoryReads, int ChangedScopeVerificationReads,
    bool AcceptedInventoryUsed, bool AcceptedTransferVerified, bool LegacyInventoryUsed);

/// <summary>Tracks the live Android status revision across repeated reads and other peer operations.</summary>
/// <remarks>2026-11-24: Prevent stale STB10 status projection for FR-FILESYNC-001#ac-layouts.</remarks>
internal sealed record AndroidAcceptanceContinuity(long Awaiting, long Ready, long Repeated, bool OtherPeerPreservesReady);

/// <summary>Verifies device inventory handling and replay determinism at the authoritative host boundary.</summary>
/// <remarks>2026-11-24: Share TEST-FILESYNC-001/002 assertions for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
internal static class StageAssertions
{
    /// <summary>Checks that the desktop owns one live monitor and stops it before leaving the presentation scope.</summary>
    public static void VerifyComposedAndroidArrival(ComposedAndroidArrivalObservation actual)
    {
        Assert.True(actual.BaselineSilent);
        Assert.True(actual.ReusedPresentation);
        Assert.Equal(1, actual.MaximumPollsInFlight);
        Assert.True(actual.SelectedLivePhone);
        Assert.True(actual.DraftPreserved);
        Assert.True(actual.PlaybackPreserved);
        Assert.True(actual.StopCancelledLongPoll);
        Assert.True(actual.DeactivatedViewModels);
    }

    /// <summary>Checks the configured formats, any staged audio, and the actual generated playlist bytes.</summary>
    public static void VerifyComposedDrop(ComposedDropObservation actual)
    {
        Assert.Equal(["musicolet:m3u8", "car-player:pls"], actual.ProfileFormats);
        Assert.Equal(["missing.flac", "missing.flac"], actual.AudioNames);
        Assert.Equal(["present.flac", "missing.flac", "present.flac"], actual.TrackOrder);
        Assert.Equal("#EXTM3U\n../Unknown Artist/Unknown Album/present.flac\n../Unknown Artist/Unknown Album/missing.flac\n../Unknown Artist/Unknown Album/present.flac\n", actual.M3u8);
        Assert.Equal("[playlist]\nFile1=../Unknown Artist/Unknown Album/present.flac\nFile2=../Unknown Artist/Unknown Album/missing.flac\nFile3=../Unknown Artist/Unknown Album/present.flac\nNumberOfEntries=3\nVersion=2\n", actual.Pls);
        Assert.Equal(["Complete.pls"], actual.FullyPresentEntries);
        Assert.True(actual.RepeatKeepsEntryIds);
        Assert.True(actual.DeviceUnchanged);
        Assert.True(actual.RejectedDuplicateLayout);
        Assert.True(actual.DropDisabledWithoutPeer);
        Assert.True(actual.SelectedPeerLoadedActiveProfile);
        Assert.True(actual.ExplicitProfileWasScoped);
        Assert.True(actual.PreviewKeptStagedPlan);
        Assert.True(actual.LeavingPeerClearedSelection);
        Assert.True(actual.ReturningPeerRestoredDefaultPlan);
        Assert.True(actual.OwnersStopped);
    }

    /// <summary>Checks the approved folder and status independently of the native provider.</summary>
    public static void VerifyAndroidAcceptance(AndroidAcceptanceObservation actual)
    {
        Assert.Equal(["AwaitingAcceptance", "Ready", "Paused", "Failed", "Unknown"], actual.Phases);
        Assert.Equal(["export-receiver-folder-scope-mismatch", "export-receiver-folder-scope-mismatch"], actual.RejectedFolders);
        Assert.True(actual.NoReceiverWrites);
        Assert.Equal(0, actual.AwaitingInventoryReads);
        Assert.Equal(0, actual.WrongScopeInventoryReads);
        Assert.Equal(2, actual.ChangedScopeVerificationReads);
        Assert.True(actual.AcceptedInventoryUsed);
        Assert.True(actual.AcceptedTransferVerified);
        Assert.True(actual.LegacyInventoryUsed);
    }

    /// <summary>Requires a revised status only for an observed change and retention through unrelated responses.</summary>
    public static void VerifyAndroidAcceptanceContinuity(AndroidAcceptanceContinuity actual)
    {
        Assert.True(actual.Ready > actual.Awaiting);
        Assert.Equal(actual.Ready, actual.Repeated);
        Assert.True(actual.OtherPeerPreservesReady);
    }

    /// <summary>Verifies the same pairing observations under scripted expectations and the authenticated production path.</summary>
    public static void VerifyPairingCommand(PairingCommandObservation actual)
    {
        Assert.Equal(401, actual.MissingCredential);
        Assert.Equal(403, actual.WrongLibrary);
        Assert.Equal(403, actual.WrongOrigin);
        Assert.Equal(400, actual.WrongRoot);
        Assert.Equal(400, actual.WrongRole);
        Assert.Equal(400, actual.WrongProfile);
        Assert.Equal(409, actual.StaleRevision);
        Assert.Equal(409, actual.ChangedIntent);
        Assert.Equal(["Android:phone-export", "Android:travel-export", "PcMirror:pc-library"], actual.ObservedOffers);
        Assert.Equal("AwaitingAcceptance", actual.AndroidPhase);
        Assert.Equal("AwaitingAcceptance", actual.PcPhase);
        Assert.Equal(["poweramp@3", "musicolet@2"], actual.InitialLayouts);
        Assert.Equal("poweramp", actual.ImplicitPlanProfile);
        Assert.True(actual.ReplayedOriginalResponse);
        Assert.True(actual.ReplayedWithoutNativeWork);
        Assert.True(actual.UnrelatedSharePreserved);
    }

    /// <summary>Checks that pairing offers the approved roots and leaves receiver acceptance in the receiving device.</summary>
    public static void VerifyMediaPairing(MediaPairingObservation actual)
    {
        Assert.Equal(["device:phone", "new:android-export", "members:pc-library"], actual.SenderWrites);
        Assert.True(actual.ReceiverWroteNothing);
        Assert.True(actual.AndroidAwaitingAcceptance);
        Assert.True(actual.PcAlreadyAccepted);
        Assert.True(actual.KeptOtherPcMembership);
        Assert.True(actual.RetryVerifiedWithoutWrites);
        Assert.Equal("pairing-receiver-identity-mismatch", actual.WrongRecipientCode);
        Assert.Equal("pairing-sender-folder-mismatch", actual.ConflictingFolderCode);
        Assert.True(actual.SourceUnchanged);
    }

    /// <summary>Checks that only a confirmed new arrival sends Normal Sync to the registered peer.</summary>
    public static void VerifyAndroidArrival(AndroidArrivalObservation actual, Guid expectedPeer)
    {
        Assert.True(actual.HistoricalIgnored);
        Assert.True(actual.DuplicateIgnored);
        Assert.True(actual.OtherPeersIgnored);
        Assert.True(actual.GapReconciledWithoutNavigation);
        Assert.True(actual.FreshAfterGapSelected);
        Assert.Equal("Normal", actual.SelectedMode);
        Assert.Equal(expectedPeer, actual.SelectedPeer);
        Assert.True(actual.OverlayAndFocusPreserved);
        Assert.Equal(403, actual.ForeignLibraryStatus);
        Assert.True(actual.DesktopReconnectSilent);
    }

    /// <summary>Checks the same registered mirror through status, command replay, recovery, and isolation.</summary>
    public static void VerifyPcMirrorCommands(PcMirrorCommandObservation actual)
    {
        Assert.Equal(["Transferring", "Paused", "Transferring"], actual.Phases);
        Assert.Equal([78L, 78L, 84L], actual.Files);
        Assert.Equal(["studio:pause", "studio:resume", "studio:rescan"], actual.NativeCalls);
        Assert.Equal("mirror-revision-changed", actual.StaleCode);
        Assert.Equal("operation-intent-mismatch", actual.ChangedIntentCode);
        Assert.True(actual.LostPauseResponseReplayed);
        Assert.True(actual.PausedProjectionSurvivedRestart);
        Assert.True(actual.OtherShareUnchanged);
        Assert.Equal(403, actual.ForeignLibraryStatus);
        Assert.True(actual.RevokedCallerRejected);
        Assert.True(actual.UnconfiguredPeerHidden);
    }

    /// <summary>Checks full-library mirror status, pause and resume, and isolation of every native write.</summary>
    public static void VerifyPcMirror(PcMirrorObservation actual)
    {
        Assert.Equal(["Transferring", "Paused", "Transferring"], actual.Phases);
        Assert.Equal([78L, 78L, 84L, 100L], actual.Synopsis);
        Assert.True(actual.Connected);
        Assert.True(actual.PausedProgressPreserved);
        Assert.Equal(["studio:pause", "studio:resume", "studio:rescan"], actual.Mutations);
        Assert.True(actual.AnotherMirrorUntouched);
        Assert.Equal("mirror-receiver-identity-mismatch", actual.WrongIdentityCode);
        Assert.Equal("mirror-receiver-folder-mismatch", actual.WrongScopeCode);
        Assert.Equal("AwaitingAcceptance", actual.AwaitingAcceptancePhase);
    }

    /// <summary>Checks that a profile assignment advances only after the approved publication commits.</summary>
    public static void VerifyProfileChange(ProfileChangeObservation actual)
    {
        Assert.Equal(["musicolet:2", "musicolet:2", "musicolet:2", "poweramp:3", "poweramp:3"], actual.ActiveProfiles);
        Assert.Equal("poweramp:3", actual.ReviewedProfile);
        Assert.Equal("Unsafe/Foreign.flac", actual.BlockedPath);
        Assert.Equal(["Unknown Artist/Unknown Album/track.flac"], actual.RemovalPaths);
        Assert.Equal(["../Resolved/Track.flac", "../Resolved/Track.flac"], actual.PlaylistPaths);
        Assert.True(actual.UnapprovedFilesUnchanged);
        Assert.Equal("export-profile-plan-exists", actual.ConflictingTargetCode);
        Assert.True(actual.ReplayedPreview);
        Assert.True(actual.InterruptedOnOldProfile);
        Assert.Equal(["Resolved/Track.flac", "playlists/Ready.m3u8"], actual.PublishedPaths);
        Assert.True(actual.RetainedForeignAndSource);
        Assert.True(actual.EntryIdsPreserved);
    }

    /// <summary>Checks that explicit and playlist-derived entries obey the documented staged-list edits.</summary>
    public static void VerifySyncListEdits(SyncListEditObservation actual)
    {
        Assert.Equal(["b:Staged", "a:Staged", "present:Present"], actual.AfterTrackDrop);
        Assert.Equal(["b:Staged", "present:Present", "c:Staged", "Second"], actual.AfterFirstRemoval);
        Assert.Equal(["b:Staged", "present:Present"], actual.AfterLastRemoval);
        Assert.Equal(actual.AfterLastRemoval, actual.Reloaded);
        Assert.Equal(["b.flac"], actual.PreviewFiles);
        Assert.True(actual.StableEntryIds);
        Assert.True(actual.LostResponseReplayed);
        Assert.Equal("export-plan-changed", actual.StaleCode);
        Assert.Equal("operation-intent-mismatch", actual.ChangedIntentCode);
        Assert.Equal(403, actual.ForeignLibraryStatus);
        Assert.True(actual.OldPreviewRejected);
        Assert.True(actual.MediaUnchanged);
    }

    /// <summary>Checks local configuration selects exact credential references and an authenticated listener.</summary>
    public static void VerifyHostLaunch(HostLaunchObservation actual)
    {
        Assert.True(actual.StartedOnHttps);
        Assert.Equal(200, actual.CorrectClientStatus);
        Assert.Equal(401, actual.UnknownClientStatus);
        Assert.Equal(403, actual.RevokedClientStatus);
        Assert.Equal(["client-active", "client-revoked", "receiver-a", "sender-a"], actual.CredentialReferences);
        Assert.True(actual.SenderResolved);
        Assert.True(actual.ReceiverResolved);
        Assert.True(actual.OtherLibraryDenied);
        Assert.True(actual.MissingClientCredentialRejected);
        Assert.True(actual.ConfigurationStayedSecretFree);
    }

    /// <summary>Checks the API, filesystem and transfer worker use the same validated startup configuration.</summary>
    public static void VerifyHostStartup(HostStartupObservation actual)
    {
        Assert.True(actual.Started);
        Assert.Equal(["musicolet", "poweramp"], actual.AvailableProfiles);
        Assert.Equal("playlists/First.m3u8", actual.FirstShareFile);
        Assert.Equal("playlists/Second.m3u8", actual.SecondShareFile);
        Assert.True(actual.FirstShareIsolated);
        Assert.True(actual.SecondShareIsolated);
        Assert.Equal(["android-a", "android-b"], actual.ScannedFolders);
        Assert.True(actual.TransfersCompleted);
        Assert.Equal(401, actual.UnauthorizedStatus);
        Assert.True(actual.StoreReopened);
        Assert.True(actual.OverlapRejected);
        Assert.True(actual.IdleBeforePublication);
        Assert.True(actual.PublicationTrackedReceiver);
        Assert.True(actual.TerminalTransferStoppedPolling);
        Assert.True(actual.PeerChangeClearedTransfer);
        Assert.True(actual.ShutdownCancelledTransferRead);
        Assert.True(actual.VerifiedCompletionRaisedAlert);
    }

    /// <summary>Checks the exact projected transfer, explicit retry and receiver progress.</summary>
    public static void VerifyExportTransferTransport(ExportTransferTransportObservation actual, Guid expectedTransfer, Guid expectedShare)
    {
        Assert.Equal(expectedTransfer, actual.TransferId);
        Assert.Equal(expectedShare, actual.ShareId);
        Assert.Equal(401, actual.UnauthorizedStatus);
        Assert.Equal(403, actual.OtherLibraryStatus);
        Assert.Equal("Queued", actual.FailedPhase);
        Assert.True(actual.CanRetry);
        Assert.Equal("export-scan-unavailable", actual.FailureCode);
        Assert.Equal("export-transfer-changed", actual.StaleCode);
        Assert.True(actual.LostRetryResponse);
        Assert.True(actual.RetryReceiptReplayed);
        Assert.Equal(1, actual.RevisionIncrease);
        Assert.Equal("Running", actual.PartialPhase);
        Assert.Equal(4, actual.PartialBytes);
        Assert.Equal("Complete", actual.CompletePhase);
        Assert.Equal(9, actual.CompletedBytes);
        Assert.True(actual.RetryDisabledAfterCompletion);
        Assert.True(actual.SameFilePublication);
    }

    /// <summary>Checks completion requires the published data version and the reviewed removal.</summary>
    public static void VerifyExportArrival(ExportArrivalObservation actual)
    {
        Assert.Equal(["Music/new.flac", "Playlists/Ready.m3u8", "Playlists/Retired.m3u8"], actual.ReviewedPaths);
        Assert.Equal(4, actual.FirstBytes);
        Assert.Equal(9, actual.TotalBytes);
        Assert.True(actual.WrongVersionIsPending);
        Assert.True(actual.BytesWithoutRemovalIsPending);
        Assert.True(actual.UnstableIndexIsPending);
        Assert.True(actual.CompletedAfterMatchingTombstone);
        Assert.Equal(9, actual.FinalBytes);
        Assert.True(actual.FinalReceiptSurvivesRestart);
        Assert.True(actual.SourceUnchanged);
        Assert.True(actual.ChangedSenderBlocked);
    }

    /// <summary>Checks that the durable command is retried safely and reports only sender acknowledgement.</summary>
    public static void VerifyExportScan(ExportScanObservation actual)
    {
        Assert.True(actual.QueueEmptyBeforeCommit);
        Assert.NotEqual(Guid.Empty, actual.PublicationId);
        Assert.Equal(actual.PublicationId, actual.TransferId);
        Assert.Equal("Queued", actual.InitialState);
        Assert.Equal("export-scan-unavailable", actual.FirstFailureCode);
        Assert.Equal(2, actual.AttemptsAfterRestart);
        Assert.Equal("AwaitingReceiver", actual.FinalState);
        Assert.Equal(["android #1", "android #1"], actual.RequestedFolders);
        Assert.True(actual.WrongSenderBlocked);
        Assert.True(actual.SharedWithAnotherPeerBlocked);
        Assert.True(actual.ReplayedReceipt);
        Assert.True(actual.NoFurtherDispatch);
    }

    /// <summary>Checks playlist rewrites, owned path changes, and private staging before approval.</summary>
    public static void VerifyLayoutCorrection(LayoutCorrectionObservation actual)
    {
        string[] expectedPaths = ["New/Move.flac", "New/Fresh.flac", "New/Move.flac"];
        Assert.Equal("Unsafe/Foreign.flac", actual.CollisionPath);
        Assert.Equal("export-preview-blocked", actual.BlockedCode);
        Assert.Equal(4, actual.ReviewedRevision);
        Assert.Equal(expectedPaths, actual.OrderedPaths);
        Assert.Equal(["Replace:Lists/Reviewed.m3u8", "Add:New/Fresh.flac", "Move:Old/Move.flac->New/Move.flac", "Remove:Old/Fresh.flac"], actual.Changes);
        Assert.Equal(["Old/Fresh.flac"], actual.RemovalPaths);
        Assert.True(actual.OldExportRetainedUntilApproval);
        Assert.Equal("export-removal-approval-stale", actual.StaleApprovalCode);
        Assert.Equal("#EXTM3U\n../New/Move.flac\n../New/Fresh.flac\n../New/Move.flac\n", actual.Playlist);
        Assert.Equal("moving audio", actual.MovedAudio);
        Assert.Equal("fresh audio", actual.AddedAudio);
        Assert.True(actual.OldPathsGone);
        Assert.Equal("outside ownership", actual.ForeignFile);
        Assert.Equal("fresh audio", actual.Source);
        Assert.Equal(expectedPaths, actual.ReloadedPaths);
    }

    /// <summary>Checks that the authorized commands return an exact preview and replay one applied publication.</summary>
    public static void VerifyDeviceExportCommands(DeviceExportCommandObservation actual)
    {
        Assert.Equal(401, actual.MissingCredentialStatus);
        Assert.Equal(403, actual.WrongLibraryStatus);
        Assert.Equal(403, actual.WrongOriginStatus);
        Assert.Equal("export-plan-unavailable", actual.WrongScopeCode);
        Assert.Equal(2, actual.PreviewRevision);
        Assert.Equal(["Add:Music/Missing.flac", "Add:Playlists/Ready.m3u8"], actual.Changes);
        Assert.True(actual.ShareEmptyAtPreview);
        Assert.Equal("export-plan-changed", actual.StalePublishCode);
        Assert.True(actual.PreviewResponseLost);
        Assert.True(actual.PublishResponseLost);
        Assert.NotEqual(Guid.Empty, actual.PublicationId);
        Assert.Equal(actual.PublicationId, actual.TransferId);
        Assert.True(actual.ReceiptReplayed);
        Assert.Equal(1, actual.ManifestRevision);
    }

    /// <summary>Checks the exact prepared playlist and missing audio survive restart and later source edits.</summary>
    public static void VerifyStagedPlaylistPublish(StagedPlaylistPublishObservation actual)
    {
        Assert.Equal(2, actual.PreviewRevision);
        Assert.Equal(["Library/Missing.flac", "Lists/Headless.m3u8"], actual.ChangedPaths);
        Assert.True(actual.ShareEmptyAtPreview);
        Assert.Equal("reviewed audio", actual.PublishedAudio);
        Assert.Equal("#EXTM3U\n../Library/Present.flac\n../Library/Missing.flac\n", actual.PublishedPlaylist);
        Assert.Equal("changed after preview", actual.SourceAfterPreview);
        Assert.Equal(1, actual.ManifestRevision);
        Assert.True(actual.ReceiptReplayed);
        Assert.Equal("export-plan-changed", actual.StalePreviewCode);
    }

    /// <summary>Checks that file effects and retry preserve the source library and approved scope.</summary>
    public static void VerifyExportFileEffects(ExportFileEffectsObservation actual)
    {
        Assert.Equal("new audio", actual.Added);
        Assert.Equal("new playlist", actual.Replaced);
        Assert.Equal("move audio", actual.Moved);
        Assert.True(actual.OldMoveRemoved);
        Assert.True(actual.RetiredRemoved);
        Assert.Equal("new audio", actual.Source);
        Assert.Equal("outside share", actual.OtherShare);
        Assert.Equal("export-file-changed", actual.UnexpectedConflictCode);
        Assert.True(actual.Replayed);
        Assert.Equal(["move audio", "old playlist", "retired playlist"], actual.RetainedOriginals);
    }

    /// <summary>Checks that interruption and replay preserve the exact publication and previous ownership.</summary>
    public static void VerifyExportJournal(ExportJournalObservation actual)
    {
        Assert.NotEqual(Guid.Empty, actual.ReservedId);
        Assert.Equal(actual.ReservedId, actual.ReopenedId);
        Assert.Equal(1, actual.ReopenedNextStep);
        Assert.Equal(0, actual.BeforeManifestRevision);
        Assert.Equal(1, actual.FinalManifestRevision);
        Assert.Equal(["Library/A.flac", "Lists/Ready.m3u8"], actual.OwnedPaths);
        Assert.Equal(actual.ReservedId, actual.ReplayedReceipt);
        Assert.Equal("export-publication-in-progress", actual.CompetingCode);
        Assert.Equal("export-publication-incomplete", actual.IncompleteCode);
        Assert.Equal("operation-intent-mismatch", actual.ChangedIntentCode);
    }

    /// <summary>Checks that a share read uses the observed bytes and returns only selected paths.</summary>
    public static void VerifyExportShareRead(ExportShareReadObservation actual)
    {
        Assert.Equal("Library/Existing.flac", actual.OwnedPath);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", actual.OwnedDigest);
        Assert.Equal("Collision", actual.ObstructingPath);
        Assert.Equal("File", actual.ObstructingKind);
        Assert.True(actual.MissingPathAbsent);
        Assert.True(actual.OtherFileAbsent);
        Assert.Equal("export-share-scope-mismatch", actual.WrongScopeCode);
    }

    /// <summary>Checks publication preview ownership and approval without depending on a file adapter.</summary>
    public static void VerifyExportPreview(ExportPreviewObservation actual)
    {
        Assert.Equal(["Current/New.flac"], actual.Added);
        Assert.Equal(["Old/Move.flac -> Current/Move.flac"], actual.Moved);
        Assert.Equal(["Old/Retired.m3u8"], actual.Removed);
        Assert.Equal(["Old/Retired.m3u8"], actual.ApprovedPaths);
        Assert.Equal(["export-destination-collision", "export-owned-content-changed", "export-source-unavailable"], actual.ValidationCodes);
        Assert.True(actual.MatchingApprovalAccepted);
        Assert.True(actual.StaleApprovalRejected);
        Assert.True(actual.ForeignFileRemainsUnowned);
    }

    /// <summary>Verifies that a reload returns the same staged list and cannot open another scope.</summary>
    public static void VerifyDeviceExportRead(DeviceExportReadObservation actual, Guid planId, Guid[] entries)
    {
        Assert.Equal(planId, actual.PlanId);
        Assert.Equal(1, actual.PlanRevision);
        Assert.Equal(entries, actual.OrderedEntryIds);
        Assert.Equal(["musicolet", "poweramp"], actual.Profiles);
        Assert.True(actual.AlternatePlanEmpty);
        Assert.Equal("export-plan-unavailable", actual.WrongDeviceCode);
        Assert.Equal("export-plan-unavailable", actual.WrongProfileCode);
        Assert.Equal("export-read-access-denied", actual.RevokedOriginCode);
    }

    /// <summary>Checks that staging uses the selected device and authoritative library data.</summary>
    public static void VerifyRegisteredLayout(RegisteredLayoutObservation actual)
    {
        Assert.Equal("owned-android-share", actual.ShareId);
        Assert.Equal("m3u8", actual.Format);
        Assert.Equal("playlists/Night Drive.m3u8", actual.PlaylistPath);
        Assert.Equal(["Lena Vale/Northern Lines/B.flac", "Lena Vale/Northern Lines/A.flac", "Lena Vale/Northern Lines/B.flac"], actual.OrderedPaths);
        Assert.Equal(["Lena Vale/Northern Lines/A.flac"], actual.QueuedAudio);
        Assert.Equal("android-export-target-unavailable", actual.WrongLibraryCode);
        Assert.Equal("export-profile-changed", actual.StaleProfileCode);
        Assert.Equal("export-registration-changed", actual.ChangedRegistrationCode);
    }

    /// <summary>Checks documented Android paths and rejects ambiguous or unsupported profiles.</summary>
    public static void VerifyAndroidLayout(AndroidLayoutObservation actual)
    {
        Assert.Equal("musicolet", actual.ProfileId);
        Assert.Equal(2, actual.Revision);
        Assert.Equal("/storage/emulated/0/Music/Beets", actual.Root);
        Assert.Equal("Lena Vale/Northern Lines/01 Harbor Lights.flac", actual.AudioPath);
        Assert.Equal("playlists/Night Drive.m3u8", actual.PlaylistPath);
        Assert.Equal("Lists/Night Drive.pls", actual.AlternatePlaylistPath);
        Assert.Equal("[playlist]\nFile1=../Library/B.flac\nFile2=../Library/A.flac\nFile3=../Library/B.flac\nNumberOfEntries=3\nVersion=2\n",
            actual.AlternateContent);
        Assert.Equal("invalid-device-layout-path", actual.UnsafePathCode);
        Assert.Equal("invalid-device-layout-yaml", actual.DuplicateKeyCode);
        Assert.Equal("unsupported-playlist-format", actual.UnsupportedFormatCode);
    }

    /// <summary>Checks exact-path inventory, explicit uncertainty and protection of occupied destinations.</summary>
    public static void VerifyReceiverInventory(ReceiverInventoryObservation actual)
    {
        Assert.Equal(["Music/Already there.flac"], actual.Present);
        Assert.Equal(["Music/Song #2.flac"], actual.Missing);
        Assert.Equal("export-inventory-device-mismatch", actual.WrongDeviceCode);
        Assert.Equal("export-inventory-index-changed", actual.ChangedIndexCode);
        Assert.Equal("unsafe-device-destination", actual.UnsafeEntryCode);
        Assert.Equal("export-inventory-destination-hidden", actual.HiddenDestinationCode);
        Assert.Equal("export-inventory-unavailable", actual.UnexpectedNotFoundCode);
    }

    public static void VerifyTransport(StageTransportObservation actual, Guid operation, Guid device)
    {
        Assert.Equal(401, actual.MissingCredentialStatus);
        Assert.Equal(403, actual.WrongLibraryStatus);
        Assert.Equal(operation, actual.OperationId);
        Assert.Equal(device, actual.DeviceId);
        Assert.Equal("musicolet", actual.ProfileId);
        Assert.Equal(2, actual.ProfileRevision);
        Assert.Equal(1, actual.PlanRevision);
        Assert.Equal(409, actual.ChangedIntentStatus);
    }

    public static void Verify(StageObservation stage, StageObservation replay)
    {
        Assert.NotEqual(Guid.Empty, stage.PlanId);
        Assert.Equal(1, stage.Revision);
        Assert.Equal(["B", "A", "B"], stage.OrderedReferences);
        Assert.Equal(["A"], stage.StagedAudio);
        Assert.Equal("#EXTM3U\n../Music/B.flac\n../Music/A.flac\n../Music/B.flac\n", stage.PlaylistContent);
        Assert.Equal(stage.PlanId, replay.PlanId);
        Assert.Equal(stage.Revision, replay.Revision);
        Assert.Equal(stage.OrderedReferences, replay.OrderedReferences);
        Assert.Equal(stage.StagedAudio, replay.StagedAudio);
        Assert.Equal(stage.PlaylistContent, replay.PlaylistContent);
    }
}
