using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the device playlist staging receipt assertions independently of the host store.</summary>
/// <remarks>2026-11-24: Exercise TEST-FILESYNC-001 for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class DevicePlaylistStageMockTests
{
    /// <summary>Proves the desktop arrival lifecycle assertions detect premature navigation and a leaked monitor.</summary>
    /// <remarks>2026-11-25: Verify independent controls before the FR-FILESYNC-001#ac-arrival composition changes.</remarks>
    [Fact]
    public void ComposedArrivalRejectsAnUnownedOrUnstoppedMonitor()
    {
        var fixture = new ComposedAndroidArrivalObservation(true, true, 1, true, true, true, true, true);
        StageAssertions.VerifyComposedAndroidArrival(fixture);
        ComposedAndroidArrivalObservation[] wrong =
        [
            fixture with { BaselineSilent = false },
            fixture with { ReusedPresentation = false },
            fixture with { MaximumPollsInFlight = 2 },
            fixture with { SelectedLivePhone = false },
            fixture with { DraftPreserved = false },
            fixture with { PlaybackPreserved = false },
            fixture with { StopCancelledLongPoll = false },
            fixture with { DeactivatedViewModels = false }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyComposedAndroidArrival(observation)));
    }

    /// <summary>Proves the configured desktop drop assertions recognize the wrong profile, bytes, or staged entries.</summary>
    /// <remarks>2026-11-25: Reject independent wrong outcomes for FR-FILESYNC-001#ac-layouts and ac-playlist-drop before composing the routes.</remarks>
    [Fact]
    public void ComposedDropRejectsTheWrongFormatOrInventory()
    {
        var fixture = new ComposedDropObservation(["musicolet:m3u8", "car-player:pls"], ["missing.flac", "missing.flac"],
            ["present.flac", "missing.flac", "present.flac"],
            "#EXTM3U\n../Unknown Artist/Unknown Album/present.flac\n../Unknown Artist/Unknown Album/missing.flac\n../Unknown Artist/Unknown Album/present.flac\n",
            "[playlist]\nFile1=../Unknown Artist/Unknown Album/present.flac\nFile2=../Unknown Artist/Unknown Album/missing.flac\nFile3=../Unknown Artist/Unknown Album/present.flac\nNumberOfEntries=3\nVersion=2\n",
            ["Complete.pls"], true, true, true, true, true, true, true, true, true, true);
        StageAssertions.VerifyComposedDrop(fixture);
        ComposedDropObservation[] wrong =
        [
            fixture with { ProfileFormats = ["musicolet:m3u8", "car-player:m3u8"] },
            fixture with { AudioNames = ["present.flac", "missing.flac", "missing.flac"] },
            fixture with { TrackOrder = ["present.flac", "missing.flac"] },
            fixture with { M3u8 = "#EXTM3U\n" },
            fixture with { Pls = "[playlist]\nNumberOfEntries=0\nVersion=2\n" },
            fixture with { FullyPresentEntries = ["present.flac", "Complete.pls"] },
            fixture with { RepeatKeepsEntryIds = false },
            fixture with { DeviceUnchanged = false },
            fixture with { RejectedDuplicateLayout = false },
            fixture with { DropDisabledWithoutPeer = false },
            fixture with { SelectedPeerLoadedActiveProfile = false },
            fixture with { ExplicitProfileWasScoped = false },
            fixture with { PreviewKeptStagedPlan = false },
            fixture with { LeavingPeerClearedSelection = false },
            fixture with { ReturningPeerRestoredDefaultPlan = false },
            fixture with { OwnersStopped = false }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyComposedDrop(observation)));
    }

    /// <summary>Checks that new observations advance the status revision and unrelated commands preserve them.</summary>
    /// <remarks>2026-11-24: Distinguish persisted Android share status from historical pairing receipts for FR-FILESYNC-001#ac-layouts.</remarks>
    [Fact]
    public void AndroidAcceptanceContinuityRejectsStaleSnapshots()
    {
        var fixture = new AndroidAcceptanceContinuity(2, 3, 3, true);
        StageAssertions.VerifyAndroidAcceptanceContinuity(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptanceContinuity(fixture with { Awaiting = 3 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptanceContinuity(fixture with { Repeated = 4 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptanceContinuity(fixture with { OtherPeerPreservesReady = false }));
    }

    /// <summary>Proves acceptance, scope and index assertions using scripted receiver outcomes.</summary>
    /// <remarks>2026-11-24: Prove STB10 pairing and TEST-FILESYNC-002 for FR-FILESYNC-001#ac-layouts, ac-playlist-drop, and ac-safety.</remarks>
    [Fact]
    public void AndroidAcceptanceAssertionsRejectUnverifiedReceiverState()
    {
        var fixture = new AndroidAcceptanceObservation(["AwaitingAcceptance", "Ready", "Paused", "Failed", "Unknown"],
            ["export-receiver-folder-scope-mismatch", "export-receiver-folder-scope-mismatch"], true, 0, 0, 2, true, true, true);
        StageAssertions.VerifyAndroidAcceptance(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { Phases = ["Ready", "Ready", "Ready", "Ready", "Ready"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { RejectedFolders = ["accepted", "accepted"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { NoReceiverWrites = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { AwaitingInventoryReads = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { WrongScopeInventoryReads = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { ChangedScopeVerificationReads = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { AcceptedInventoryUsed = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { AcceptedTransferVerified = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidAcceptance(fixture with { LegacyInventoryUsed = false }));
    }

    /// <summary>Proves pairing assertions against approved, rejected, and replayed proposals before production binding.</summary>
    /// <remarks>2026-11-24: Prove STB10 outcomes for FR-FILESYNC-001#ac-mirror, ac-layouts and ac-safety.</remarks>
    [Fact]
    public void PairingCommandAssertionsRejectForeignScopeAndDuplicateOffers()
    {
        var fixture = new PairingCommandObservation(401, 403, 403, 400, 400, 400, 409, 409,
            ["Android:phone-export", "Android:travel-export", "PcMirror:pc-library"],
            "AwaitingAcceptance", "AwaitingAcceptance", ["poweramp@3", "musicolet@2"], "poweramp", true, true, true);
        StageAssertions.VerifyPairingCommand(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { MissingCredential = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { WrongLibrary = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { WrongOrigin = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { WrongRoot = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { WrongRole = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { WrongProfile = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { StaleRevision = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { ChangedIntent = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { ObservedOffers = ["Android:wrong-folder"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { AndroidPhase = "Complete" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { PcPhase = "Complete" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { InitialLayouts = ["musicolet@2", "musicolet@2"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { ImplicitPlanProfile = "musicolet" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { ReplayedOriginalResponse = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { ReplayedWithoutNativeWork = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPairingCommand(fixture with { UnrelatedSharePreserved = false }));
    }

    /// <summary>Rejects incorrect receiver consent and source-folder changes before binding pairing.</summary>
    /// <remarks>2026-11-24: Prove the native offer assertions for FR-FILESYNC-001#ac-mirror, ac-layouts and ac-safety.</remarks>
    [Fact]
    public void MediaPairingAssertionsRejectCrossShareAndImplicitAcceptance()
    {
        var fixture = new MediaPairingObservation(["device:phone", "new:android-export", "members:pc-library"], true, true, true, true,
            true, "pairing-receiver-identity-mismatch", "pairing-sender-folder-mismatch", true);
        StageAssertions.VerifyMediaPairing(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { SenderWrites = ["new:wrong-folder"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { ReceiverWroteNothing = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { AndroidAwaitingAcceptance = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { PcAlreadyAccepted = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { KeptOtherPcMembership = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { RetryVerifiedWithoutWrites = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { WrongRecipientCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { ConflictingFolderCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyMediaPairing(fixture with { SourceUnchanged = false }));
    }

    /// <summary>Discriminates current Android connections from duplicate, foreign, and historical delivery.</summary>
    /// <remarks>2026-11-24: Prove STB21 assertions for FR-FILESYNC-001#ac-arrival.</remarks>
    [Fact]
    public void AndroidArrivalAssertionsRejectFalseNavigationAndLostModal()
    {
        var peer = Guid.NewGuid();
        var fixture = new AndroidArrivalObservation(true, true, true, true, true, "Normal", peer, true, 403, true);
        StageAssertions.VerifyAndroidArrival(fixture, peer);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { HistoricalIgnored = false }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { DuplicateIgnored = false }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { OtherPeersIgnored = false }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { GapReconciledWithoutNavigation = false }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { FreshAfterGapSelected = false }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { SelectedMode = "Minimum" }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { SelectedPeer = Guid.NewGuid() }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { OverlayAndFocusPreserved = false }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { ForeignLibraryStatus = 200 }, peer));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidArrival(fixture with { DesktopReconnectSilent = false }, peer));
    }

    /// <summary>Rejects stale PC mirror results, duplicated effects and wrong-scope state.</summary>
    /// <remarks>2026-11-24: Prove the registered CQRS assertions for FR-FILESYNC-001#ac-mirror.</remarks>
    [Fact]
    public void PcMirrorCommandAssertionsRejectStaleAndDuplicatedEffects()
    {
        var fixture = new PcMirrorCommandObservation(["Transferring", "Paused", "Transferring"], [78L, 78L, 84L],
            ["studio:pause", "studio:resume", "studio:rescan"], "mirror-revision-changed", "operation-intent-mismatch",
            true, true, true, 403, true, true);
        StageAssertions.VerifyPcMirrorCommands(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { Phases = ["Transferring", "Complete"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { Files = [78L, 0L, 84L] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { NativeCalls = ["studio:pause", "studio:pause"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { StaleCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { ChangedIntentCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { LostPauseResponseReplayed = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { PausedProjectionSurvivedRestart = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { OtherShareUnchanged = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { ForeignLibraryStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { RevokedCallerRejected = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirrorCommands(fixture with { UnconfiguredPeerHidden = false }));
    }

    /// <summary>Rejects leaked control effects and false PC mirror progress before binding the native adapter.</summary>
    /// <remarks>2026-11-24: Prove the STB10 PC mirror assertions for FR-FILESYNC-001#ac-mirror.</remarks>
    [Fact]
    public void PcMirrorAssertionsRejectCrossPeerControlsAndInventedProgress()
    {
        var fixture = new PcMirrorObservation(["Transferring", "Paused", "Transferring"], [78L, 78L, 84L, 100L], true, true,
            ["studio:pause", "studio:resume", "studio:rescan"], true, "mirror-receiver-identity-mismatch",
            "mirror-receiver-folder-mismatch", "AwaitingAcceptance");
        StageAssertions.VerifyPcMirror(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { Phases = ["Transferring", "Transferring", "Transferring"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { Synopsis = [78L, 0L, 100L, 100L] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { Connected = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { PausedProgressPreserved = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { Mutations = ["studio:pause", "other:resume", "studio:rescan"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { AnotherMirrorUntouched = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { WrongIdentityCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { WrongScopeCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyPcMirror(fixture with { AwaitingAcceptancePhase = "Complete" }));
    }

    /// <summary>Rejects premature assignment and cross-plan replacement for cross-profile review.</summary>
    /// <remarks>2026-11-24: Prove STB20 assertions for FR-FILESYNC-001#ac-layouts and ac-safety before binding the host.</remarks>
    [Fact]
    public void ProfileChangeAssertionsDetectPrematureAssignmentAndUnownedRemoval()
    {
        var fixture = new ProfileChangeObservation(["musicolet:2", "musicolet:2", "musicolet:2", "poweramp:3", "poweramp:3"],
            "poweramp:3", "Unsafe/Foreign.flac", ["Unknown Artist/Unknown Album/track.flac"],
            ["../Resolved/Track.flac", "../Resolved/Track.flac"], true, "export-profile-plan-exists", true, true,
            ["Resolved/Track.flac", "playlists/Ready.m3u8"], true, true);
        StageAssertions.VerifyProfileChange(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { ActiveProfiles = ["poweramp:3"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { ReviewedProfile = "musicolet:2" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { BlockedPath = "unrelated" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { RemovalPaths = ["Unsafe/Foreign.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { PlaylistPaths = ["../Resolved/Track.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { UnapprovedFilesUnchanged = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { ConflictingTargetCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { ReplayedPreview = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { InterruptedOnOldProfile = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { PublishedPaths = ["Unsafe/Foreign.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { RetainedForeignAndSource = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyProfileChange(fixture with { EntryIdsPreserved = false }));
    }

    /// <summary>Rejects incorrect staged intent, unsafe writes, and conflicting replay independently of the host.</summary>
    /// <remarks>2026-11-24: Prove the STB10 track/drop removal assertions for FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public void SyncListEditAssertionsRejectLostDependenciesAndPrematureFilesystemEdits()
    {
        var fixture = new SyncListEditObservation(["b:Staged", "a:Staged", "present:Present"],
            ["b:Staged", "present:Present", "c:Staged", "Second"], ["b:Staged", "present:Present"], ["b:Staged", "present:Present"],
            ["b.flac"], true, true, "export-plan-changed", "operation-intent-mismatch", 403, true, true);
        StageAssertions.VerifySyncListEdits(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { AfterTrackDrop = ["a:Staged", "b:Staged", "present:Staged"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { AfterFirstRemoval = ["b:Staged", "present:Present", "Second"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { AfterLastRemoval = ["present:Present"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { Reloaded = ["present:Present"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { PreviewFiles = ["b.flac", "present.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { StableEntryIds = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { LostResponseReplayed = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { StaleCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { ChangedIntentCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { ForeignLibraryStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { OldPreviewRejected = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifySyncListEdits(fixture with { MediaUnchanged = false }));
    }

    /// <summary>Rejects credential scope leaks and unauthenticated launch before binding the standalone entry point.</summary>
    /// <remarks>2026-11-24: Prove launcher assertions for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public void HostLaunchAssertionsRejectWrongCredentialRolesAndInsecureStartup()
    {
        var fixture = new HostLaunchObservation(true, 200, 401, 403, ["client-active", "client-revoked", "receiver-a", "sender-a"],
            true, true, true, true, true);
        StageAssertions.VerifyHostLaunch(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { StartedOnHttps = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { CorrectClientStatus = 403 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { UnknownClientStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { RevokedClientStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { CredentialReferences = ["client-active", "receiver-other"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { SenderResolved = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { ReceiverResolved = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { OtherLibraryDenied = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { MissingClientCredentialRejected = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostLaunch(fixture with { ConfigurationStayedSecretFree = false }));
    }

    /// <summary>Rejects incomplete host startup and cross-share effects before wiring production services.</summary>
    /// <remarks>2026-11-24: Prove the FR-FILESYNC-001#ac-layouts and ac-safety startup assertions.</remarks>
    [Fact]
    public void HostStartupAssertionsRejectCrossShareAndMissingWorker()
    {
        var fixture = new HostStartupObservation(true, ["musicolet", "poweramp"], "playlists/First.m3u8", "playlists/Second.m3u8",
            true, true, ["android-a", "android-b"], true, 401, true, true, true, true, true, true, true, true);
        StageAssertions.VerifyHostStartup(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { Started = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { AvailableProfiles = ["musicolet"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { FirstShareFile = "wrong" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { SecondShareFile = "wrong" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { FirstShareIsolated = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { SecondShareIsolated = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { ScannedFolders = ["android-a", "android-a"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { TransfersCompleted = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { UnauthorizedStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { StoreReopened = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { OverlapRejected = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { IdleBeforePublication = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { PublicationTrackedReceiver = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { TerminalTransferStoppedPolling = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { PeerChangeClearedTransfer = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { ShutdownCancelledTransferRead = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyHostStartup(fixture with { VerifiedCompletionRaisedAlert = false }));
    }

    /// <summary>Proves the desktop transfer assertions reject scope, replay and progress errors before implementation.</summary>
    /// <remarks>2026-11-24: Mock the FR-FILESYNC-001#ac-safety transfer command results.</remarks>
    [Fact]
    public void ExportTransferTransportRejectsCrossScopeAndDuplicateRetry()
    {
        var expectedId = Guid.NewGuid();
        var shareId = Guid.NewGuid();
        var fixture = new ExportTransferTransportObservation(expectedId, shareId, 401, 403, "Queued", true, "export-scan-unavailable",
            "export-transfer-changed", true, true, 1, "Running", 4, "Complete", 9, true, true);
        StageAssertions.VerifyExportTransferTransport(fixture, expectedId, shareId);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { TransferId = Guid.Empty }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { ShareId = Guid.NewGuid() }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { UnauthorizedStatus = 200 }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { OtherLibraryStatus = 200 }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { FailedPhase = "Complete" }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { CanRetry = false }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { FailureCode = "unexpected" }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { StaleCode = "accepted" }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { LostRetryResponse = false }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { RetryReceiptReplayed = false }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { RevisionIncrease = 2 }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { PartialPhase = "Complete" }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { PartialBytes = 9 }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { CompletePhase = "Running" }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { CompletedBytes = 4 }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { RetryDisabledAfterCompletion = false }, expectedId, shareId));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportTransferTransport(fixture with { SameFilePublication = false }, expectedId, shareId));
    }

    /// <summary>Rejects incorrect sender bytes and optimistic receiver completion before implementation.</summary>
    /// <remarks>2026-11-24: Prove evidence for FR-FILESYNC-001#ac-safety and TEST-FILESYNC-001/002.</remarks>
    [Fact]
    public void ExportArrivalAssertionsRejectStaleFileVersionsAndMissingTombstones()
    {
        var fixture = new ExportArrivalObservation(["Music/new.flac", "Playlists/Ready.m3u8", "Playlists/Retired.m3u8"], 4, 9,
            true, true, true, true, 9, true, true, true);
        StageAssertions.VerifyExportArrival(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { ReviewedPaths = ["Music/new.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { FirstBytes = 9 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { TotalBytes = 4 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { WrongVersionIsPending = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { BytesWithoutRemovalIsPending = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { UnstableIndexIsPending = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { CompletedAfterMatchingTombstone = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { FinalBytes = 8 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { FinalReceiptSurvivesRestart = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { SourceUnchanged = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportArrival(fixture with { ChangedSenderBlocked = false }));
    }

    /// <summary>Rejects early scans, unscoped requests and false receiver completion before implementation.</summary>
    /// <remarks>2026-11-24: Prove the scan scheduling assertions for FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public void ExportScanAssertionsRejectEarlyAndUnscopedScans()
    {
        var publication = Guid.NewGuid();
        var fixture = new ExportScanObservation(true, publication, publication, "Queued", "export-scan-unavailable", 2, "AwaitingReceiver",
            ["android #1", "android #1"], true, true, true, true);
        StageAssertions.VerifyExportScan(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { QueueEmptyBeforeCommit = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { PublicationId = Guid.Empty }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { TransferId = Guid.NewGuid() }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { InitialState = "Complete" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { FirstFailureCode = "" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { AttemptsAfterRestart = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { FinalState = "Complete" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { RequestedFolders = ["", "other"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { WrongSenderBlocked = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { SharedWithAnotherPeerBlocked = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { ReplayedReceipt = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportScan(fixture with { NoFurtherDispatch = false }));
    }

    /// <summary>Proves the layout review assertions discriminate wrong paths and unapproved effects.</summary>
    /// <remarks>2026-11-24: Prove the layout correction coverage before binding FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public void LayoutCorrectionAssertionsRejectLossOfOwnershipAndPlaylistOrder()
    {
        var order = new[] { "New/Move.flac", "New/Fresh.flac", "New/Move.flac" };
        var fixture = new LayoutCorrectionObservation("Unsafe/Foreign.flac", "export-preview-blocked", 4, order,
            ["Replace:Lists/Reviewed.m3u8", "Add:New/Fresh.flac", "Move:Old/Move.flac->New/Move.flac", "Remove:Old/Fresh.flac"],
            ["Old/Fresh.flac"], true, "export-removal-approval-stale", "#EXTM3U\n../New/Move.flac\n../New/Fresh.flac\n../New/Move.flac\n",
            "moving audio", "fresh audio", true, "outside ownership", "fresh audio", order);
        StageAssertions.VerifyLayoutCorrection(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { CollisionPath = "" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { BlockedCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { ReviewedRevision = 3 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { OrderedPaths = [.. order.Distinct()] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { Changes = ["Add:New/Fresh.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { RemovalPaths = ["Unsafe/Foreign.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { OldExportRetainedUntilApproval = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { StaleApprovalCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { Playlist = "#EXTM3U\n" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { MovedAudio = "missing" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { AddedAudio = "old audio" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { OldPathsGone = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { ForeignFile = "overwritten" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { Source = "deleted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyLayoutCorrection(fixture with { ReloadedPaths = ["Old/Move.flac"] }));
    }

    /// <summary>Separately rejects access, review, replay and transfer claim failures before binding the HTTPS tests.</summary>
    /// <remarks>2026-11-24: Prove the TEST-FILESYNC-001/002 assertions for the FR-FILESYNC-001#ac-safety transport.</remarks>
    [Fact]
    public void DeviceExportCommandAssertionsRejectWrongScopeAndUnreplayedPublication()
    {
        var publication = Guid.NewGuid();
        var fixture = new DeviceExportCommandObservation(401, 403, 403, "export-plan-unavailable", 2,
            ["Add:Music/Missing.flac", "Add:Playlists/Ready.m3u8"], true, "export-plan-changed", true, true, publication, publication, true, 1);
        StageAssertions.VerifyDeviceExportCommands(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { MissingCredentialStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { WrongLibraryStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { WrongOriginStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { WrongScopeCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { PreviewRevision = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { Changes = ["Remove:Unrelated.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { ShareEmptyAtPreview = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { StalePublishCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { PreviewResponseLost = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { PublishResponseLost = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { PublicationId = Guid.Empty }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { TransferId = Guid.NewGuid() }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { ReceiptReplayed = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportCommands(fixture with { ManifestRevision = 2 }));
    }

    [Fact]
    public void StagedPublishAssertionsRejectEarlyWritesAndUnreviewedBytes()
    {
        var fixture = new StagedPlaylistPublishObservation(2, ["Library/Missing.flac", "Lists/Headless.m3u8"], true,
            "reviewed audio", "#EXTM3U\n../Library/Present.flac\n../Library/Missing.flac\n", "changed after preview", 1, true, "export-plan-changed");
        StageAssertions.VerifyStagedPlaylistPublish(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { PreviewRevision = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { ChangedPaths = [.. fixture.ChangedPaths, "Library/Present.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { ShareEmptyAtPreview = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { PublishedAudio = "changed after preview" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { PublishedPlaylist = "#EXTM3U\n" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { SourceAfterPreview = "reviewed audio" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { ManifestRevision = 0 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { ReceiptReplayed = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyStagedPlaylistPublish(fixture with { StalePreviewCode = "accepted" }));
    }

    [Fact]
    public void FileEffectAssertionsRejectUnsafeChangesAndLostOriginals()
    {
        var fixture = new ExportFileEffectsObservation("new audio", "new playlist", "move audio", true, true, "new audio", "outside share",
            "export-file-changed", true, ["move audio", "old playlist", "retired playlist"]);
        StageAssertions.VerifyExportFileEffects(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { Added = "wrong bytes" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { Replaced = "old playlist" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { Moved = "" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { OldMoveRemoved = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { RetiredRemoved = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { Source = "no longer present" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { OtherShare = "overwritten" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { UnexpectedConflictCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { Replayed = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportFileEffects(fixture with { RetainedOriginals = [] }));
    }

    [Fact]
    public void JournalAssertionsRejectPrematureOwnershipAndChangedReplay()
    {
        var id = Guid.NewGuid();
        var fixture = new ExportJournalObservation(id, id, 1, 0, 1, ["Library/A.flac", "Lists/Ready.m3u8"], id,
            "export-publication-in-progress", "export-publication-incomplete", "operation-intent-mismatch");
        StageAssertions.VerifyExportJournal(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { ReservedId = Guid.Empty }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { ReopenedId = Guid.NewGuid() }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { ReopenedNextStep = 2 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { BeforeManifestRevision = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { FinalManifestRevision = 2 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { OwnedPaths = ["Other/Unowned.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { ReplayedReceipt = Guid.NewGuid() }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { CompetingCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { IncompleteCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportJournal(fixture with { ChangedIntentCode = "accepted" }));
    }

    [Fact]
    public void ShareReadAssertionsRejectAssumedPresenceAndWrongContent()
    {
        var fixture = new ExportShareReadObservation("Library/Existing.flac",
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "Collision", "File", true, true, "export-share-scope-mismatch");
        StageAssertions.VerifyExportShareRead(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { OwnedPath = "library/existing.flac" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { OwnedDigest = "wrong" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { ObstructingPath = "Collision/Track.flac" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { ObstructingKind = "Directory" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { MissingPathAbsent = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { OtherFileAbsent = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportShareRead(fixture with { WrongScopeCode = "accepted" }));
    }

    [Fact]
    public void PublicationPreviewAssertionsRejectUnownedOrStaleRemoval()
    {
        var fixture = new ExportPreviewObservation(["Current/New.flac"], ["Old/Move.flac -> Current/Move.flac"], ["Old/Retired.m3u8"],
            ["Old/Retired.m3u8"], ["export-destination-collision", "export-owned-content-changed", "export-source-unavailable"],
            MatchingApprovalAccepted: true, StaleApprovalRejected: true, ForeignFileRemainsUnowned: true);
        StageAssertions.VerifyExportPreview(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { Added = [] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { Moved = ["Current/Move.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { Removed = ["Other/Unowned.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { ApprovedPaths = [.. fixture.ApprovedPaths, "Other/Unowned.flac"] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { ValidationCodes = [] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { MatchingApprovalAccepted = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { StaleApprovalRejected = false }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyExportPreview(fixture with { ForeignFileRemainsUnowned = false }));
    }

    [Fact]
    public void ReloadAssertionsRejectCrossDevicePlansAndLostOrder()
    {
        var id = Guid.NewGuid();
        var entries = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var fixture = new DeviceExportReadObservation(id, 1, entries, ["musicolet", "poweramp"], true,
            "export-plan-unavailable", "export-plan-unavailable", "export-read-access-denied");
        StageAssertions.VerifyDeviceExportRead(fixture, id, entries);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { PlanId = Guid.NewGuid() }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { PlanRevision = 2 }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { OrderedEntryIds = [.. entries.Reverse()] }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { Profiles = ["poweramp"] }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { AlternatePlanEmpty = false }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { WrongDeviceCode = "accepted" }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { WrongProfileCode = "accepted" }, id, entries));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyDeviceExportRead(fixture with { RevokedOriginCode = "accepted" }, id, entries));
    }

    [Fact]
    public void RegisteredLayoutAssertionsRejectCrossScopeAndStaleDestinationWrites()
    {
        var fixture = new RegisteredLayoutObservation("owned-android-share", "m3u8", "playlists/Night Drive.m3u8",
            ["Lena Vale/Northern Lines/B.flac", "Lena Vale/Northern Lines/A.flac", "Lena Vale/Northern Lines/B.flac"],
            ["Lena Vale/Northern Lines/A.flac"], "android-export-target-unavailable", "export-profile-changed", "export-registration-changed");
        StageAssertions.VerifyRegisteredLayout(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { ShareId = "pc-mirror" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { Format = "pls" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { PlaylistPath = "shared/Other.m3u8" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { OrderedPaths = [.. fixture.OrderedPaths.Reverse().Skip(1)] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { QueuedAudio = fixture.OrderedPaths }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { WrongLibraryCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { StaleProfileCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyRegisteredLayout(fixture with { ChangedRegistrationCode = "accepted" }));
    }

    [Fact]
    public void LayoutAssertionsRejectWrongDefaultsUnsafeEditsAndIncorrectFormats()
    {
        var fixture = new AndroidLayoutObservation("musicolet", 2, "/storage/emulated/0/Music/Beets",
            "Lena Vale/Northern Lines/01 Harbor Lights.flac", "playlists/Night Drive.m3u8", "Lists/Night Drive.pls",
            "[playlist]\nFile1=../Library/B.flac\nFile2=../Library/A.flac\nFile3=../Library/B.flac\nNumberOfEntries=3\nVersion=2\n",
            "invalid-device-layout-path", "invalid-device-layout-yaml", "unsupported-playlist-format");
        StageAssertions.VerifyAndroidLayout(fixture);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { ProfileId = "poweramp" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { Revision = 1 }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { Root = "Music" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { AudioPath = "Music/01 Harbor Lights.flac" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { PlaylistPath = "Night Drive.m3u8" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { AlternatePlaylistPath = "Lists/Night Drive.m3u8" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { AlternateContent = "File1=../Library/A.flac" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { UnsafePathCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { DuplicateKeyCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyAndroidLayout(fixture with { UnsupportedFormatCode = "accepted" }));
    }

    [Fact]
    public void InventoryAssertionsDistinguishMissingFromPresentHiddenAndChanged()
    {
        var correct = new ReceiverInventoryObservation(["Music/Already there.flac"], ["Music/Song #2.flac"],
            "export-inventory-device-mismatch", "export-inventory-index-changed", "unsafe-device-destination",
            "export-inventory-destination-hidden", "export-inventory-unavailable");
        StageAssertions.VerifyReceiverInventory(correct);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { Present = [] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { Missing = ["Music/Already there.flac", .. correct.Missing] }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { WrongDeviceCode = "missing" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { ChangedIndexCode = "missing" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { UnsafeEntryCode = "missing" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { HiddenDestinationCode = "missing" }));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyReceiverInventory(correct with { UnexpectedNotFoundCode = "missing" }));
    }

    [Fact]
    public void TransportAssertionsRejectWrongAccessScopeAndReceiptIdentity()
    {
        var operation = Guid.NewGuid();
        var device = Guid.NewGuid();
        var fixture = new StageTransportObservation(401, 403, operation, device, "musicolet", 2, 1, 409);
        StageAssertions.VerifyTransport(fixture, operation, device);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { MissingCredentialStatus = 200 }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { WrongLibraryStatus = 200 }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { OperationId = Guid.NewGuid() }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { DeviceId = Guid.NewGuid() }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { ProfileId = "other-app" }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { ProfileRevision = 3 }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { PlanRevision = 2 }, operation, device));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.VerifyTransport(fixture with { ChangedIntentStatus = 200 }, operation, device));
    }

    [Fact]
    public void StagingAssertionsDetectExtraCopiesWrongOrderAndDuplicatedReplays()
    {
        var plan = Guid.NewGuid();
        var baseline = new StageObservation(plan, 1, ["B", "A", "B"], ["A"], "#EXTM3U\n../Music/B.flac\n../Music/A.flac\n../Music/B.flac\n");
        StageAssertions.Verify(baseline, baseline);
        Assert.ThrowsAny<XunitException>(() => StageAssertions.Verify(baseline with { StagedAudio = ["A", "B"] }, baseline));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.Verify(baseline with { OrderedReferences = ["A", "B"] }, baseline));
        Assert.ThrowsAny<XunitException>(() => StageAssertions.Verify(baseline, baseline with { Revision = 2 }));
    }
}
