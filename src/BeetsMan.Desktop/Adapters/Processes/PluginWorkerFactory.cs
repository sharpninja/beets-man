using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Plugin.Protocol.V1;

namespace BeetsMan.Desktop.Adapters.Processes;

/// <summary>Starts only a locally trusted package and negotiates its exact worker identity over stdio.</summary>
/// <remarks>2026-11-25: Bind package verification and bounded IPC to FR-PLUGINS-001#ac-visualize, ac-recovery and TEST-PLUGINS-002.</remarks>
public sealed class PluginWorkerFactory(LocalPluginPackages packages) : IPluginWorkerFactory
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VisualizationTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12
    };

    /// <inheritdoc />
    public async Task<Outcome<IPluginWorker>> StartAsync(InstalledPluginPackage registration, CancellationToken token)
    {
        var package = registration.Package;
        if (registration.Trust != PluginTrust.Local || !registration.Enabled || !package.Compatible)
            return new Outcome<IPluginWorker>.Invalid([new("plugin-worker-not-authorized")]);
        var prepared = await packages.PrepareWorkerAsync(package, token).ConfigureAwait(false);
        if (prepared is not Outcome<PluginWorkerFiles>.Success ready)
            return Forward(prepared);

        PluginWorkerFiles? files = ready.Value;
        BoundedTextProcess? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(HandshakeTimeout);
        try
        {
            var scopedToken = deadline.Token;
            scopedToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = files.DirectoryPath };
            info.ArgumentList.Add(files.EntryPoint);
            process = BoundedTextProcess.Start(info);
            var hello = new WorkerHello(PluginPackage.HostProtocol, "hello", Guid.NewGuid(), package.PluginId, package.Version);
            await process.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(hello, WireOptions), scopedToken).ConfigureAwait(false);
            var raw = await process.ReadAsync(stopAtLine: true, scopedToken).ConfigureAwait(false);
            if (raw is null)
                return Failure("plugin-worker-exited", true);
            var response = JsonSerializer.Deserialize<WorkerReady>(raw, WireOptions);
            if (!Validate(hello, response, package, out var contributions, out var visualizations))
                return Failure("plugin-worker-protocol-invalid", false);
            if (process.HasExited)
                return Failure("plugin-worker-exited", true);
            var worker = new OwnedWorker(process, files, package, contributions, visualizations);
            files = null;
            process = null;
            return new Outcome<IPluginWorker>.Success(worker);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { return token.IsCancellationRequested ? new Outcome<IPluginWorker>.Cancelled() : Failure("plugin-worker-timeout", true); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidDataException)
        { return Failure("plugin-worker-protocol-invalid", false); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { return Failure("plugin-worker-start-failed", true); }
        catch (IOException)
        { return Failure(process?.HasExited == true ? "plugin-worker-exited" : "plugin-worker-unavailable", true); }
        finally
        {
            try { process?.Dispose(); }
            finally { files?.Dispose(); }
        }
    }

    private static bool Validate(WorkerHello hello, WorkerReady? response, PluginPackage package,
        out ImmutableArray<PluginContribution> approved, out ImmutableArray<string> visualizations)
    {
        approved = [];
        visualizations = [];
        if (response is not { Kind: "ready", Contributions: { } offers } || response.Protocol != hello.Protocol ||
            response.RequestId != hello.RequestId || response.PluginId != hello.PluginId || response.Version != hello.Version ||
            offers.Length > package.Contributions.Length)
            return false;
        var declared = package.Contributions.ToDictionary(item => item.ContributionId, StringComparer.Ordinal);
        var offered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in offers)
        {
            if (candidate?.ContributionId is not { } id || !offered.Add(id) || !declared.TryGetValue(id, out var definition) ||
                candidate.Kind != JsonNamingPolicy.CamelCase.ConvertName(definition.Kind.ToString()) ||
                candidate.TelemetryProtocol is not null && (candidate.TelemetryProtocol != 1 || definition.Kind != PluginContributionKind.Visualization))
                return false;
        }
        approved = [.. package.Contributions.Where(item => offered.Contains(item.ContributionId) &&
            (item.Kind != PluginContributionKind.Panel || item.Panel is { } panel && offered.Contains(panel.ActionId)))];
        visualizations = [.. offers.Where(item => item.TelemetryProtocol == 1).Select(item => item.ContributionId)];
        return true;
    }

    private static Outcome<IPluginWorker> Failure(string code, bool retryable) => new Outcome<IPluginWorker>.DependencyFailure(code, retryable);

    private static Outcome<IPluginWorker> Forward(Outcome<PluginWorkerFiles> outcome) => outcome switch
    {
        Outcome<PluginWorkerFiles>.Invalid invalid => new Outcome<IPluginWorker>.Invalid(invalid.Issues),
        Outcome<PluginWorkerFiles>.DependencyFailure failure => Failure(failure.Code, failure.Retryable),
        Outcome<PluginWorkerFiles>.Conflict conflict => new Outcome<IPluginWorker>.Conflict(conflict.Code, conflict.ActualRevision),
        _ => new Outcome<IPluginWorker>.Cancelled()
    };

    private sealed class OwnedWorker(BoundedTextProcess process, PluginWorkerFiles files, PluginPackage package,
        ImmutableArray<PluginContribution> contributions, ImmutableArray<string> visualizations)
        : IPluginWorker
    {
        private readonly SemaphoreSlim exchanges = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private int disposed;
        private int closed;
        private string? failure;

        public ImmutableArray<PluginContribution> Contributions { get; } = contributions;
        public ImmutableArray<string> Visualizations { get; } = visualizations;
        public string? FailureCode => Volatile.Read(ref failure);

        public bool HasExited
        {
            get
            {
                try { return Volatile.Read(ref closed) != 0 || process.HasExited; }
                catch (InvalidOperationException) when (Volatile.Read(ref closed) != 0) { return true; }
            }
        }

        /// <summary>Serializes action replies and closes the channel when a reply can no longer be matched.</summary>
        /// <remarks>2026-11-25: Bound the worker action exchange for FR-PLUGINS-001#ac-visualize and ac-recovery.</remarks>
        public Task<Outcome<PluginWorkerActionResult>> InvokeActionAsync(Guid operationId, string contributionId,
            ImmutableDictionary<string, string> arguments, CancellationToken token)
        {
            if (!Contributions.Any(item => item.Kind == PluginContributionKind.Action && item.ContributionId == contributionId))
                return Task.FromResult<Outcome<PluginWorkerActionResult>>(new Outcome<PluginWorkerActionResult>.Invalid([new("plugin-action-unavailable")]));
            var request = new WorkerAction(PluginPackage.HostProtocol, "invoke", Guid.NewGuid(), operationId, package.PluginId, package.Version,
                contributionId, arguments);
            return ExchangeAsync<PluginWorkerActionResult>(request, line =>
            {
                var reply = JsonSerializer.Deserialize<WorkerActionResult>(line, WireOptions);
                if (reply is not { Kind: "result", Message: { Length: <= 256 } message } || reply.Protocol != request.Protocol ||
                    reply.RequestId != request.RequestId || reply.PluginId != request.PluginId || reply.Version != request.Version ||
                    reply.ContributionId != request.ContributionId || message.Any(char.IsControl) || reply.HostAction is { Length: > 128 })
                    throw new InvalidDataException();
                return new(message, reply.HostAction);
            }, token);
        }

        /// <inheritdoc />
        public Task<Outcome<ImmutableArray<float>?>> RenderVisualizationAsync(Guid sessionId, string contributionId,
            PlaybackVisualizationFrame frame, CancellationToken token)
        {
            if (!Visualizations.Contains(contributionId))
                return Task.FromResult<Outcome<ImmutableArray<float>?>>(new Outcome<ImmutableArray<float>?>.Invalid([new("plugin-visualization-unavailable")]));
            var request = new WorkerVisualization(PluginPackage.HostProtocol, "visualize", Guid.NewGuid(), package.PluginId, package.Version,
                contributionId, sessionId, frame.Sequence, frame.SampleRate, frame.Waveform, frame.Spectrum);
            return ExchangeAsync<ImmutableArray<float>?>(request, line =>
            {
                var reply = JsonSerializer.Deserialize<WorkerVisualizationResult>(line, WireOptions);
                if (reply is not { Kind: "frame" } || reply.Protocol != request.Protocol || reply.RequestId != request.RequestId ||
                    reply.PluginId != request.PluginId || reply.Version != request.Version || reply.ContributionId != request.ContributionId ||
                    reply.SessionId != request.SessionId || reply.Sequence <= 0 || reply.Sequence > request.Sequence ||
                    reply.Levels.IsDefaultOrEmpty || reply.Levels.Length > PlaybackVisualizationFrame.WaveformPoints ||
                    reply.Levels.Any(level => !float.IsFinite(level) || level is < 0 or > 1))
                    throw new InvalidDataException();
                return reply.Sequence == request.Sequence ? reply.Levels : null;
            }, token, skipWhenBusy: true);
        }

        private async Task<Outcome<TResult>> ExchangeAsync<TResult>(object request, Func<string, TResult> decode, CancellationToken token,
            bool skipWhenBusy = false)
        {
            try
            {
                if (skipWhenBusy)
                {
                    if (!await exchanges.WaitAsync(0, token).ConfigureAwait(false))
                        return new Outcome<TResult>.Cancelled();
                }
                else
                    await exchanges.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<TResult>.Cancelled(); }
            try
            {
                if (Volatile.Read(ref disposed) != 0 || HasExited)
                    return new Outcome<TResult>.DependencyFailure("plugin-worker-exited", true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                timeout.CancelAfter(skipWhenBusy ? VisualizationTimeout : HandshakeTimeout);
                var deadline = timeout.Token;
                try
                {
                    await process.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, WireOptions), deadline).ConfigureAwait(false);
                    var line = await process.ReadAsync(stopAtLine: true, deadline).ConfigureAwait(false);
                    if (line is null)
                        return FailExchange<TResult>("plugin-worker-exited", true);
                    return new Outcome<TResult>.Success(decode(line));
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    if (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                        return FailExchange<TResult>("plugin-worker-timeout", true);
                    Close(skipWhenBusy ? "plugin-visualization-interrupted" : "plugin-action-interrupted");
                    return new Outcome<TResult>.Cancelled();
                }
                catch (Exception error) when (error is InvalidDataException or DecoderFallbackException or JsonException)
                { return FailExchange<TResult>("plugin-worker-protocol-invalid", false); }
                catch (IOException) { return FailExchange<TResult>(HasExited ? "plugin-worker-exited" : "plugin-worker-unavailable", true); }
            }
            finally { exchanges.Release(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            lifetime.Cancel();
            exchanges.Wait();
            try { Close(null); }
            finally
            {
                lifetime.Dispose();
                exchanges.Release();
            }
        }

        private Outcome<TResult> FailExchange<TResult>(string code, bool retryable)
        {
            Close(code);
            return new Outcome<TResult>.DependencyFailure(code, retryable);
        }

        private void Close(string? code)
        {
            if (Volatile.Read(ref closed) != 0)
                return;
            Volatile.Write(ref failure, code);
            Volatile.Write(ref closed, 1);
            try { process.Dispose(); }
            finally { files.Dispose(); }
        }
    }
}
