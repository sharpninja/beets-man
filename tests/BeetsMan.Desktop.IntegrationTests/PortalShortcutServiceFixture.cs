using System.Collections.Concurrent;
using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Serves the XDG shortcut protocol over a private dbus-daemon without a compositor or input devices.</summary>
/// <remarks>2026-11-25: Exercise real bus framing, request races, failure and closure for TEST-SETTINGS-002.</remarks>
internal sealed class PortalShortcutServiceFixture : IPathMethodHandler, IAsyncDisposable
{
    private const string Desktop = "/org/freedesktop/portal/desktop";
    private readonly Process daemon;
    private readonly DBusConnection bus;
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentDictionary<string, HostRegistration> hostIdentities = new(StringComparer.Ordinal);
    private readonly bool requireHostIdentity;
    private int requestOrder;

    /// <summary>2026-11-25: Select the legacy or identity-aware TEST-SETTINGS-002 portal.</summary>
    private PortalShortcutServiceFixture(Process daemon, string address, bool requireHostIdentity)
    {
        this.daemon = daemon;
        this.requireHostIdentity = requireHostIdentity;
        Address = address;
        bus = new(address);
    }

    public string Path => Desktop;
    public bool HandlesChildPaths => false;
    internal string Address { get; }
    internal ConcurrentQueue<Session> Bound { get; } = new();
    internal ConcurrentQueue<Session> Closed { get; } = new();
    internal ConcurrentQueue<string> HeldRequests { get; } = new();
    internal ConcurrentQueue<string> ClosedRequests { get; } = new();

    /// <summary>2026-11-25: Run the configured XDG portal fixture for FR-SETTINGS-001#ac-shortcuts.</summary>
    internal static async Task<PortalShortcutServiceFixture> OpenAsync(CancellationToken token, bool requireHostIdentity = false)
    {
        var start = new ProcessStartInfo("dbus-daemon") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "--session", "--nofork", "--print-address=1" })
            start.ArgumentList.Add(argument);
        var daemon = Process.Start(start) ?? throw new InvalidOperationException("Private D-Bus could not start.");
        PortalShortcutServiceFixture? result = null;
        try
        {
            var address = await daemon.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token)
                ?? throw new InvalidOperationException("Private D-Bus did not publish its address.");
            result = new(daemon, address, requireHostIdentity);
            await result.bus.ConnectAsync();
            result.bus.AddMethodHandler(result);
            await result.bus.RequestNameAsync("org.freedesktop.portal.Desktop");
            return result;
        }
        catch
        {
            if (result is not null)
                await result.DisposeAsync();
            else
                await StopDaemonAsync(daemon);
            throw;
        }
    }

    /// <summary>2026-11-25: Require host identity before portal calls in the modern TEST-SETTINGS-002 fixture.</summary>
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        switch (context.Request.MemberAsString)
        {
            case "Register" when requireHostIdentity && context.Request.InterfaceAsString == "org.freedesktop.host.portal.Registry":
                Register(context);
                break;
            case "Get":
                if (requireHostIdentity && !hostIdentities.ContainsKey(context.Request.SenderAsString!))
                {
                    context.ReplyError("org.freedesktop.portal.Error.NotAllowed", "Host identity must precede portal calls");
                    break;
                }
                using (var reply = context.CreateReplyWriter("v"))
                {
                    reply.WriteVariantUInt32(1);
                    context.Reply(reply.CreateMessage());
                }
                break;
            case "CreateSession": Create(context); break;
            case "BindShortcuts": Bind(context); break;
            case "ListShortcuts": List(context); break;
            default: context.ReplyUnknownMethodError(); break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>2026-11-25: Register a fixture peer with an explicit application ID for TEST-SETTINGS-002.</summary>
    private void Register(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var applicationId = reader.ReadString();
        var peer = context.Request.SenderAsString!;
        if (reader.ReadDictionaryOfStringToVariantValue().Count != 0 ||
            !hostIdentities.TryAdd(peer, new(applicationId, peer, Interlocked.Increment(ref requestOrder))))
        {
            context.ReplyError("org.freedesktop.portal.Error.Failed", "Unexpected host registration");
            return;
        }
        using var reply = context.CreateReplyWriter("");
        context.Reply(reply.CreateMessage());
    }

    /// <summary>2026-11-25: Pin the TEST-SETTINGS-002 session to the peer whose application ID was registered.</summary>
    private void Create(MethodContext context)
    {
        var source = context.Request.SenderAsString!;
        var registered = hostIdentities.TryGetValue(source, out var registration);
        if (requireHostIdentity && !registered)
        {
            context.ReplyError("org.freedesktop.portal.Error.NotAllowed", "An app id is required");
            return;
        }
        var senderPath = source[1..].Replace('.', '_');
        var settings = context.Request.GetBodyReader().ReadDictionaryOfStringToVariantValue();
        var path = $"{Desktop}/session/{senderPath}/{settings["session_handle_token"].GetString()}";
        var handle = $"{Desktop}/request/{senderPath}/{settings["handle_token"].GetString()}";
        var session = new Session(path, source, registration, Interlocked.Increment(ref requestOrder));
        sessions[path] = session;
        bus.AddMethodHandler(new CloseHandler(this, session));
        Respond(context, handle, 0, new() { ["session_handle"] = path });
    }

    private void Bind(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var session = sessions[reader.ReadObjectPathAsString()];
        var slots = new List<string>();
        var array = reader.ReadArrayStart(DBusType.Struct);
        var decline = false;
        while (reader.HasNext(array))
        {
            reader.AlignStruct();
            var id = reader.ReadString();
            var specific = reader.ReadDictionaryOfStringToVariantValue();
            if (id == "playback.play-pause")
                session.PreferredTrigger = specific["preferred_trigger"].GetString();
            if (id == "playback.stop")
                decline = true;
            slots.Add(id);
        }
        session.Parent = reader.ReadString();
        var options = reader.ReadDictionaryOfStringToVariantValue();
        var handle = $"{Desktop}/request/{session.Owner[1..].Replace('.', '_')}/{options["handle_token"].GetString()}";
        session.Actions = slots.ToArray();
        Bound.Enqueue(session);
        if (session.Actions.Contains("playback.next"))
        {
            bus.AddMethodHandler(new RequestCloseHandler(this, handle));
            HeldRequests.Enqueue(handle);
            ReturnHandle(context, handle);
            return;
        }
        Respond(context, handle, decline ? 2u : 0u, decline ? new() : new() { ["shortcuts"] = Bindings(session) });
    }

    private void List(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var session = sessions[reader.ReadObjectPathAsString()];
        var options = reader.ReadDictionaryOfStringToVariantValue();
        var handle = $"{Desktop}/request/{session.Owner[1..].Replace('.', '_')}/{options["handle_token"].GetString()}";
        Respond(context, handle, 0, new() { ["shortcuts"] = Bindings(session) });
    }

    private static VariantValue Bindings(Session session) => new Tmds.DBus.Protocol.Array<Struct<string, Dict<string, VariantValue>>>(
        session.Actions.Select(id => Struct.Create(id, new Dict<string, VariantValue> { { "trigger_description", session.TriggerDescription } })));

    private void Respond(MethodContext method, string handle, uint code, Dictionary<string, VariantValue> results)
    {
        using (var signal = bus.GetMessageWriter())
        {
            // Respond before the method return to test the caller's preinstalled subscription.
            signal.WriteSignalHeader(destination: method.Request.SenderAsString, path: handle,
                @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(code);
            signal.WriteDictionary(results);
            bus.TrySendMessage(signal.CreateMessage());
        }
        ReturnHandle(method, handle);
    }

    private static void ReturnHandle(MethodContext method, string handle)
    {
        using var reply = method.CreateReplyWriter("o");
        reply.WriteObjectPath(handle);
        method.Reply(reply.CreateMessage());
    }

    internal void Activate(Session session, string id)
    {
        using var signal = bus.GetMessageWriter();
        signal.WriteSignalHeader(destination: session.Owner, path: Desktop,
            @interface: "org.freedesktop.portal.GlobalShortcuts", member: "Activated", signature: "osta{sv}");
        signal.WriteObjectPath(session.Path);
        signal.WriteString(id);
        signal.WriteUInt64(1);
        signal.WriteDictionary(new Dictionary<string, VariantValue>());
        bus.TrySendMessage(signal.CreateMessage());
    }

    internal void Revoke(Session session)
    {
        using var signal = bus.GetMessageWriter();
        signal.WriteSignalHeader(destination: session.Owner, path: session.Path,
            @interface: "org.freedesktop.portal.Session", member: "Closed", signature: "a{sv}");
        signal.WriteDictionary(new Dictionary<string, VariantValue>());
        bus.TrySendMessage(signal.CreateMessage());
    }

    internal void ChangeTrigger(Session session, string description)
    {
        session.TriggerDescription = description;
        using var signal = bus.GetMessageWriter();
        signal.WriteSignalHeader(destination: session.Owner, path: Desktop,
            @interface: "org.freedesktop.portal.GlobalShortcuts", member: "ShortcutsChanged", signature: "oa(sa{sv})");
        signal.WriteObjectPath(session.Path);
        var array = signal.WriteArrayStart(DBusType.Struct);
        foreach (var id in session.Actions)
        {
            signal.WriteStructureStart();
            signal.WriteString(id);
            signal.WriteDictionary(new Dictionary<string, VariantValue> { ["trigger_description"] = description });
        }
        signal.WriteArrayEnd(array);
        bus.TrySendMessage(signal.CreateMessage());
    }

    public async ValueTask DisposeAsync()
    {
        bus.Dispose();
        await StopDaemonAsync(daemon);
    }

    private static async Task StopDaemonAsync(Process process)
    {
        using (process)
        {
            if (!process.HasExited)
                process.Kill();
            await process.WaitForExitAsync();
        }
    }

    /// <summary>2026-11-25: Captures the creation-time application identity for TEST-SETTINGS-002.</summary>
    internal sealed class Session(string path, string owner, HostRegistration? hostRegistration, int creationOrder)
    {
        internal string Path { get; } = path;
        internal string Owner { get; } = owner;
        internal HostRegistration? Registration { get; } = hostRegistration;
        internal int CreationOrder { get; } = creationOrder;
        internal string PreferredTrigger { get; set; } = "";
        internal string? Parent { get; set; }
        internal string[] Actions { get; set; } = [];
        internal string TriggerDescription { get; set; } = "Media Play/Pause";
    }

    /// <summary>2026-11-25: Records the source connection and order of a TEST-SETTINGS-002 host registration.</summary>
    internal sealed record HostRegistration(string ApplicationId, string Peer, int Order);

    private sealed class CloseHandler(PortalShortcutServiceFixture service, Session session) : IPathMethodHandler
    {
        public string Path => session.Path;
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            using var reply = context.CreateReplyWriter("");
            context.Reply(reply.CreateMessage());
            service.Closed.Enqueue(session);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RequestCloseHandler(PortalShortcutServiceFixture service, string handle) : IPathMethodHandler
    {
        public string Path => handle;
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString != "Close")
                context.ReplyUnknownMethodError();
            else
            {
                using var reply = context.CreateReplyWriter("");
                context.Reply(reply.CreateMessage());
                service.ClosedRequests.Enqueue(handle);
            }
            return ValueTask.CompletedTask;
        }
    }
}
