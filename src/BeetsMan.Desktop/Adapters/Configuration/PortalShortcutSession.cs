using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Configuration;
using Tmds.DBus.Protocol;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Owns the D-Bus connection, correlated requests and observations for one portal binding lease.</summary>
/// <remarks>2026-11-25: Keep candidate and approved sessions independent for FR-SETTINGS-001#ac-shortcuts.</remarks>
internal sealed class PortalShortcutSession(string address, ImmutableArray<ShortcutBinding> requested,
    Action<PortalShortcutSession, ShortcutBinding> dispatch, Action<PortalShortcutSession> changed) : IAsyncDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string Path = "/org/freedesktop/portal/desktop";
    private const string RegistryInterface = "org.freedesktop.host.portal.Registry";
    private const string ApplicationId = "io.github.sharpninja.BeetsMan";
    private const string ShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string SessionInterface = "org.freedesktop.portal.Session";
    private readonly DBusConnection bus = new(address);
    private readonly List<IDisposable> watches = [];
    private readonly object gate = new();
    private ImmutableArray<ShortcutBinding> bindings = [];
    private string? owner;
    private string? sender;
    private string? session;
    private bool closed;
    private int bindingUpdate;

    internal ImmutableArray<ShortcutBinding> Bindings { get { lock (gate) return bindings; } }

    /// <summary>Subscribes before issuing each request and activates only a complete accepted set.</summary>
    /// <remarks>2026-11-25: Register the native host identity before creating a session for FR-SETTINGS-001#ac-shortcuts.</remarks>
    internal async Task<bool> BindAsync(CancellationToken token)
    {
        await bus.ConnectAsync().AsTask().WaitAsync(token).ConfigureAwait(false);
        await RegisterHostIdentityAsync(token).ConfigureAwait(false);
        if (await bus.CallMethodAsync(ReadVersion(), static (reply, _) => reply.GetBodyReader().ReadVariantValue().GetUInt32())
                .WaitAsync(token).ConfigureAwait(false) < 1)
            return false;
        owner = await bus.CallMethodAsync(FindOwner(), static (reply, _) => reply.GetBodyReader().ReadString())
            .WaitAsync(token).ConfigureAwait(false);
        if (bus.UniqueName is not { Length: > 1 } unique || !owner.StartsWith(':'))
            throw new InvalidDataException();
        sender = unique[1..].Replace('.', '_');
        _ = ObserveDisconnectAsync();
        var sessionToken = NewToken();
        session = $"{Path}/session/{sender}/{sessionToken}";
        var created = await RequestAsync(handle => CreateSession(handle, sessionToken), token).ConfigureAwait(false);
        if (created.Code != 0)
            return false;
        if (!created.Values.TryGetValue("session_handle", out var value) ||
            value.Type != VariantValueType.String || value.GetString() != session)
            throw new InvalidDataException();

        watches.Add(await bus.WatchSignalAsync(owner, session, SessionInterface, "Closed", _ => MarkClosed(),
            readerState: null, emitOnCapturedContext: false, flags: ObserverFlags.None).AsTask().WaitAsync(token).ConfigureAwait(false));
        watches.Add(await bus.WatchSignalAsync(owner, Path, ShortcutsInterface, "Activated", ReadActivation,
            (error, activated) => { if (error is null) Deliver(activated.Session, activated.Id); },
            readerState: null, emitOnCapturedContext: false, flags: ObserverFlags.None).AsTask().WaitAsync(token).ConfigureAwait(false));
        watches.Add(await bus.WatchSignalAsync(owner, Path, ShortcutsInterface, "ShortcutsChanged",
            static (message, _) => message.GetBodyReader().ReadObjectPathAsString(),
            (error, changedSession) => { if (error is null && changedSession == session) _ = RefreshBindingsAsync(); },
            readerState: null, emitOnCapturedContext: false, flags: ObserverFlags.None).AsTask().WaitAsync(token).ConfigureAwait(false));
        var accepted = await RequestAsync(BindShortcuts, token).ConfigureAwait(false);
        if (accepted.Code != 0 || !accepted.Values.TryGetValue("shortcuts", out var shortcuts) || DecodeBindings(shortcuts) is not { } actual)
            return false;
        lock (gate)
        {
            if (closed)
                return false;
            bindings = actual;
        }
        return true;
    }

    /// <summary>Associates this connection with the installed desktop entry before portal calls.</summary>
    /// <remarks>2026-11-25: Keep legacy and sandbox-provided identities eligible for FR-SETTINGS-001#ac-shortcuts.</remarks>
    private async Task RegisterHostIdentityAsync(CancellationToken token)
    {
        try
        {
            MessageBuffer registration;
            using (var writer = bus.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: RegistryInterface, member: "Register", signature: "sa{sv}");
                writer.WriteString(ApplicationId);
                writer.WriteDictionary(new Dictionary<string, VariantValue>());
                registration = writer.CreateMessage();
            }
            await bus.CallMethodAsync(registration).WaitAsync(token).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException error) when (error.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod"
            or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.AccessDenied"
            or "org.freedesktop.portal.Error.Failed")
        {
            // The shortcut portal still checks the identity assigned to the peer by the platform.
        }
    }

    public async ValueTask DisposeAsync()
    {
        MarkClosed();
        foreach (var watch in watches)
            watch.Dispose();
        if (owner is not null && session is not null)
            await TryCloseAsync(session, SessionInterface).ConfigureAwait(false);
        bus.Dispose();
    }

    private async Task<Response> RequestAsync(Func<string, MessageBuffer> build, CancellationToken token)
    {
        var id = NewToken();
        var handle = $"{Path}/request/{sender}/{id}";
        var response = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await bus.WatchSignalAsync(owner!, handle, RequestInterface, "Response", ReadResponse, (error, reply) =>
        {
            if (error is null)
                response.TrySetResult(reply);
            else
                response.TrySetException(error);
        }, readerState: null, emitOnCapturedContext: false, flags: ObserverFlags.None).AsTask().WaitAsync(token).ConfigureAwait(false);
        try
        {
            var returned = await bus.CallMethodAsync(build(id), static (reply, _) => reply.GetBodyReader().ReadObjectPathAsString())
                .WaitAsync(token).ConfigureAwait(false);
            if (returned != handle)
                throw new InvalidDataException();
            return await response.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await TryCloseAsync(handle, RequestInterface).ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryCloseAsync(string target, string iface)
    {
        try
        {
            await bus.CallMethodAsync(CloseMessage(target, iface)).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or TimeoutException or ObjectDisposedException) { }
    }

    private MessageBuffer CloseMessage(string target, string iface)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: owner, path: target, @interface: iface, member: "Close");
        return writer.CreateMessage();
    }

    private async Task ObserveDisconnectAsync()
    {
        try { await bus.DisconnectedAsync().ConfigureAwait(false); }
        finally { MarkClosed(); }
    }

    private void MarkClosed()
    {
        bool removed;
        lock (gate)
        {
            closed = true;
            removed = !bindings.IsEmpty;
            bindings = [];
        }
        if (removed)
            changed(this);
    }

    private async Task RefreshBindingsAsync()
    {
        var revision = Interlocked.Increment(ref bindingUpdate);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var reply = await RequestAsync(ListShortcuts, deadline.Token).ConfigureAwait(false);
            if (reply.Code != 0 || !reply.Values.TryGetValue("shortcuts", out var value))
                return;
            var actual = DecodeBindings(value);
            lock (gate)
            {
                if (closed || bindings.IsEmpty || Volatile.Read(ref bindingUpdate) != revision)
                    return;
                bindings = actual ?? [];
                if (actual is null)
                    closed = true;
            }
            changed(this);
            if (actual is null && session is not null)
                await TryCloseAsync(session, SessionInterface).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private void Deliver(string path, string id)
    {
        if (session != path || !Bindings.Any(binding => binding.ActionId == id))
            return;
        if (requested.FirstOrDefault(binding => binding.ActionId == id) is { } binding)
            dispatch(this, binding);
    }

    private ImmutableArray<ShortcutBinding>? DecodeBindings(VariantValue value)
    {
        if (value.Type != VariantValueType.Array || value.Count != requested.Length)
            return null;
        var byId = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < value.Count; index++)
        {
            var item = value.GetItem(index);
            if (item.Type != VariantValueType.Struct || item.Count != 2 ||
                item.GetItem(0) is not { Type: VariantValueType.String } key || item.GetItem(1) is not { Type: VariantValueType.Dictionary } data)
                throw new InvalidDataException();
            var info = data.GetDictionary<string, VariantValue>();
            var description = info.GetValueOrDefault("trigger_description");
            var text = description.Type == VariantValueType.String ? description.GetString() : null;
            if (!byId.TryAdd(key.GetString(), !string.IsNullOrWhiteSpace(text) && text.Length <= 256 && !text.Any(char.IsControl) ? text : null))
                throw new InvalidDataException();
        }
        return requested.All(binding => byId.ContainsKey(binding.ActionId))
            ? [.. requested.Select(binding => binding with { SystemTriggerDescription = byId[binding.ActionId] })]
            : null;
    }

    private MessageBuffer ReadVersion()
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString(ShortcutsInterface);
        writer.WriteString("version");
        return writer.CreateMessage();
    }

    private MessageBuffer FindOwner()
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus", member: "GetNameOwner", signature: "s");
        writer.WriteString(Service);
        return writer.CreateMessage();
    }

    private MessageBuffer CreateSession(string token, string sessionToken)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: owner, path: Path, @interface: ShortcutsInterface, member: "CreateSession", signature: "a{sv}");
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token, ["session_handle_token"] = sessionToken });
        return writer.CreateMessage();
    }

    private MessageBuffer BindShortcuts(string token)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: owner, path: Path, @interface: ShortcutsInterface, member: "BindShortcuts", signature: "oa(sa{sv})sa{sv}");
        writer.WriteObjectPath(session!);
        var items = writer.WriteArrayStart(DBusType.Struct);
        foreach (var binding in requested)
        {
            writer.WriteStructureStart();
            writer.WriteString(binding.ActionId);
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["description"] = binding.ActionId switch
                {
                    ShortcutCatalog.PlayPause => "Play or pause",
                    ShortcutCatalog.SkipNext => "Next track",
                    ShortcutCatalog.SkipPrevious => "Previous track",
                    _ => "Stop playback"
                },
                ["preferred_trigger"] = XdgShortcutGesture.Format(binding.Gesture)
            });
        }
        writer.WriteArrayEnd(items);
        writer.WriteString("");
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        return writer.CreateMessage();
    }

    private MessageBuffer ListShortcuts(string token)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: owner, path: Path, @interface: ShortcutsInterface, member: "ListShortcuts", signature: "oa{sv}");
        writer.WriteObjectPath(session!);
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        return writer.CreateMessage();
    }

    private static Response ReadResponse(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return new(reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
    }

    private static (string Session, string Id) ReadActivation(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadObjectPathAsString(), reader.ReadString());
    }

    private static string NewToken() => $"beets_man_{Guid.NewGuid():N}";
    private sealed record Response(uint Code, Dictionary<string, VariantValue> Values);
}
