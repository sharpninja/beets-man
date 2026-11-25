using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Prepares and publishes the staged playlist list for a configured Android share.</summary>
/// <remarks>2026-11-24: Retain the reviewed audio and playlist bytes across retries and host restart for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class StagedDeviceExportWorkflow(IDeviceExportPreviewStore previews, IHostDeviceExportSources catalog,
    IDeviceExportPublicationJournal journal, IDeviceExportShareReader share, HostDeviceExportPublisher publisher,
    IDeviceExportPayloadStore payloads, string musicRoot, DeviceExportLayoutEditor? layoutEditor = null,
    DeviceExportProfileEditor? profileEditor = null)
{
    private const int DescriptorVersion = 1;
    private readonly HostExportMedia media = new(musicRoot);
    private readonly DeviceExportPublicationPlanner planner = new();

    /// <summary>Prepares staged additions or path corrections while keeping all staged bytes outside the active share.</summary>
    public async Task<HostOutcome<PreparedDeviceExportPublication>> PrepareAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPreviewIntent intent, CancellationToken token)
    {
        try
        {
            var read = await previews.ReadPreviewSourcesAsync(libraryId, caller, intent, token).ConfigureAwait(false);
            if (read is not HostOutcome<DeviceExportPreviewPreparation>.Success { Value: var preparation })
                return read.PropagateFailure<PreparedDeviceExportPublication>();
            if (preparation is DeviceExportPreviewPreparation.Replay replay)
                return await ReadDescriptorAsync(replay.Digest, token).ConfigureAwait(false);
            var inputs = ((DeviceExportPreviewPreparation.New)preparation).Sources;
            var plan = inputs.Plan with { PlanRevision = checked(intent.ExpectedPlanRevision + 1) };
            var prepared = await CreatePreviewAsync(inputs with { Plan = plan }, intent, token).ConfigureAwait(false);
            if (prepared is not HostOutcome<PreparedDeviceExportPublication>.Success { Value: var preview })
                return prepared;

            using var descriptor = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new PreviewDescriptor(DescriptorVersion, preview)), writable: false);
            var digest = await payloads.StoreAsync(descriptor, token).ConfigureAwait(false);
            var saved = await previews.SavePreviewAsync(libraryId, caller, intent, inputs, digest, token).ConfigureAwait(false);
            if (saved is not HostOutcome<string>.Success { Value: var committedDigest })
                return saved.PropagateFailure<PreparedDeviceExportPublication>();
            return digest == committedDigest ? prepared : await ReadDescriptorAsync(committedDigest, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<PreparedDeviceExportPublication>.Cancelled(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<PreparedDeviceExportPublication>.Unavailable("export-preview-content-unavailable");
        }
    }

    private async Task<HostOutcome<PreparedDeviceExportPublication>> CreatePreviewAsync(DeviceExportPreviewSources inputs,
        DeviceExportPreviewIntent intent, CancellationToken token)
    {
        ImmutableArray<ReviewedDeviceExportEntry> layoutEntries = [];
        ImmutableHashSet<string>? retirePaths = null;
        DeviceExportSourceProfile? previous = null;
        HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>? corrected = null;
        if (inputs.Plan.ProfileId != intent.ProfileId || inputs.Plan.ProfileRevision != intent.ProfileRevision)
        {
            if (profileEditor is null)
                return inputs.Plan.ProfileId == intent.ProfileId
                    ? new HostOutcome<PreparedDeviceExportPublication>.Rejected("export-profile-changed", inputs.Plan.ProfileRevision, Conflict: true)
                    : new HostOutcome<PreparedDeviceExportPublication>.Rejected("export-plan-unavailable");
            previous = new(inputs.Plan.ProfileId, inputs.Plan.ProfileRevision);
            corrected = await profileEditor.PrepareAsync(inputs, intent, token).ConfigureAwait(false);
        }
        else if (intent.RelativePathsByTrack is { Count: > 0 } edits)
        {
            if (layoutEditor is null)
                return new HostOutcome<PreparedDeviceExportPublication>.Unavailable("export-layout-editor-unavailable");
            corrected = await layoutEditor.PrepareAsync(inputs, edits, token).ConfigureAwait(false);
        }
        if (corrected is not null)
        {
            if (corrected is not HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Success { Value: var layout })
                return corrected.PropagateFailure<PreparedDeviceExportPublication>();
            if (!layout.Issues.IsEmpty)
                return new HostOutcome<PreparedDeviceExportPublication>.Success(new(layout.Inputs.Plan, inputs.Destination,
                    inputs.Manifest.Revision, [], layout.Issues, inputs.Manifest.Files, null) { PreviousProfile = previous });
            inputs = layout.Inputs;
            layoutEntries = layout.Entries;
            retirePaths = layout.RetirePaths;
        }

        var files = await StageCandidatesAsync(inputs, token).ConfigureAwait(false);
        if (files is not HostOutcome<ImmutableArray<DeviceExportFileCandidate>>.Success { Value: var candidates })
            return files.PropagateFailure<PreparedDeviceExportPublication>();
        var observation = await share.ReadAsync(inputs.Destination,
            candidates.Select(file => file.Path).Concat(inputs.Manifest.Files.Select(file => file.Path)), token).ConfigureAwait(false);
        if (observation is not HostOutcome<DeviceExportShareSnapshot>.Success { Value: var snapshot })
            return observation.PropagateFailure<PreparedDeviceExportPublication>();

        var mode = layoutEntries.IsEmpty && previous is null ? DeviceExportPublicationMode.AddToShare : DeviceExportPublicationMode.ReconcileLayout;
        var prepared = planner.Prepare(inputs.Plan, inputs.Destination, inputs.Manifest, snapshot, mode, candidates, retirePaths, confirmMoves: previous is not null);
        return prepared is HostOutcome<PreparedDeviceExportPublication>.Success { Value: var preview }
            ? new HostOutcome<PreparedDeviceExportPublication>.Success(preview with { LayoutEntries = layoutEntries, PreviousProfile = previous })
            : prepared;
    }

    /// <summary>Applies the reviewed bytes or resumes the same operation from its recorded progress.</summary>
    public async Task<HostOutcome<DeviceExportPublicationReservation>> PublishAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, CancellationToken token)
    {
        try
        {
            var previous = await journal.FindPublicationAsync(libraryId, caller, intent, token).ConfigureAwait(false);
            if (previous is not HostOutcome<DeviceExportPublicationReservation?>.Success { Value: var reservation })
                return previous.PropagateFailure<DeviceExportPublicationReservation>();
            if (reservation is null)
            {
                var selected = await previews.ReadPreviewDigestAsync(libraryId, caller, intent.PlanId, intent.PlanRevision, token).ConfigureAwait(false);
                if (selected is not HostOutcome<string>.Success { Value: var digest })
                    return selected.PropagateFailure<DeviceExportPublicationReservation>();
                var prepared = await ReadDescriptorAsync(digest, token).ConfigureAwait(false);
                if (prepared is not HostOutcome<PreparedDeviceExportPublication>.Success { Value: var preview })
                    return prepared.PropagateFailure<DeviceExportPublicationReservation>();
                var reserved = await journal.ReservePublicationAsync(libraryId, caller, intent, preview, token).ConfigureAwait(false);
                if (reserved is not HostOutcome<DeviceExportPublicationReservation>.Success { Value: var saved })
                    return reserved;
                reservation = saved;
            }

            if (reservation is not DeviceExportPublicationReservation.Pending)
                return new HostOutcome<DeviceExportPublicationReservation>.Success(reservation);
            var resumed = await publisher.ResumeAsync(libraryId, caller, intent, token).ConfigureAwait(false);
            return resumed is HostOutcome<CompletedDeviceExportPublication>.Success { Value: var receipt }
                ? new HostOutcome<DeviceExportPublicationReservation>.Success(new DeviceExportPublicationReservation.Completed(receipt))
                : resumed.PropagateFailure<DeviceExportPublicationReservation>();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<DeviceExportPublicationReservation>.Cancelled(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<DeviceExportPublicationReservation>.Unavailable("export-preview-content-unavailable");
        }
    }

    private async Task<HostOutcome<ImmutableArray<DeviceExportFileCandidate>>> StageCandidatesAsync(DeviceExportPreviewSources inputs,
        CancellationToken token)
    {
        var target = inputs.Destination;
        var stagedAudio = inputs.Plan.Entries.OfType<ExportEntry.Audio>().Where(audio => audio.Status == ExportAudioStatus.Staged);
        var catalogRead = await catalog.ReadDevicePlaylistSourcesAsync(target.LibraryId, target.DeviceId,
            stagedAudio.Select(audio => audio.TrackId), token).ConfigureAwait(false);
        if (catalogRead is not HostOutcome<DevicePlaylistSources>.Success { Value: var catalogInputs })
            return catalogRead.PropagateFailure<ImmutableArray<DeviceExportFileCandidate>>();
        if (catalogInputs.Destination != target)
            return new HostOutcome<ImmutableArray<DeviceExportFileCandidate>>.Rejected("export-registration-changed",
                catalogInputs.Destination.Revision, Conflict: true);

        var candidates = ImmutableArray.CreateBuilder<DeviceExportFileCandidate>(inputs.Plan.Entries.Length);
        foreach (var entry in inputs.Plan.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry is ExportEntry.Audio audio)
            {
                string? digest = null;
                if (audio.Status == ExportAudioStatus.Staged && catalogInputs.Tracks.TryGetValue(audio.TrackId, out var track))
                {
                    await using var source = media.Open(track.RelativePath);
                    if (source is not null)
                        digest = await payloads.StoreAsync(source, token).ConfigureAwait(false);
                }
                candidates.Add(new(new(ExportContentKind.Audio, audio.TrackId), audio.Destination,
                    audio.Status != ExportAudioStatus.Present, digest is not null, digest));
            }
            else if (entry is ExportEntry.Playlist playlist)
            {
                inputs.PlaylistDigests.TryGetValue(playlist.EntryId, out var digest);
                await using var source = digest is null ? null : await payloads.OpenDigestAsync(digest, token).ConfigureAwait(false);
                var readable = source is not null && await MatchesDigestAsync(source, digest!, token).ConfigureAwait(false);
                candidates.Add(new(new(ExportContentKind.Playlist, playlist.PlaylistId), playlist.Destination, true, readable, digest));
            }
        }
        return new HostOutcome<ImmutableArray<DeviceExportFileCandidate>>.Success(candidates.MoveToImmutable());
    }

    private async Task<HostOutcome<PreparedDeviceExportPublication>> ReadDescriptorAsync(string digest, CancellationToken token)
    {
        await using var stream = await payloads.OpenDigestAsync(digest, token).ConfigureAwait(false);
        if (stream is null || !await MatchesDigestAsync(stream, digest, token).ConfigureAwait(false))
            return new HostOutcome<PreparedDeviceExportPublication>.Unavailable("export-preview-content-unavailable");
        stream.Position = 0;
        try
        {
            var descriptor = await JsonSerializer.DeserializeAsync<PreviewDescriptor>(stream, cancellationToken: token).ConfigureAwait(false);
            if (descriptor is { Version: DescriptorVersion, Preview: { } preview })
                return new HostOutcome<PreparedDeviceExportPublication>.Success(preview);
        }
        catch (JsonException) { }
        return new HostOutcome<PreparedDeviceExportPublication>.Unavailable("export-preview-content-unreadable");
    }

    private static async Task<bool> MatchesDigestAsync(Stream source, string digest, CancellationToken token) =>
        string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)), digest, StringComparison.OrdinalIgnoreCase);

    private sealed record PreviewDescriptor(int Version, PreparedDeviceExportPublication Preview);
}
