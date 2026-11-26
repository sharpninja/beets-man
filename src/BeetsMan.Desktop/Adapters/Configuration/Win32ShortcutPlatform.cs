using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Owns thread-scoped Windows playback hotkeys without creating a window.</summary>
/// <remarks>2026-11-25: Register opt-in keys and release rejected replacements for FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002.</remarks>
internal sealed class Win32ShortcutPlatform(IWin32ShortcutApi api) : ISystemPlaybackShortcutPlatform
{
    private readonly object gate = new();
    private readonly BlockingCollection<ApplyRequest> pending = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private bool closed;
    private int nextId;

    public event Action<ShortcutBinding>? Activated;

    public Task<Outcome<bool>> ApplyAsync(ImmutableArray<ShortcutBinding> bindings, CancellationToken token)
    {
        var mapped = ImmutableArray.CreateBuilder<(NativeHotkey Hotkey, ShortcutBinding Binding)>(bindings.Length);
        foreach (var binding in bindings)
        {
            if (NativeHotkey.FromGesture(binding.Gesture) is not { } key)
                return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Invalid([new("system-playback-gesture-unsupported", binding.Gesture)]));
            mapped.Add((key, binding));
        }
        var request = new ApplyRequest(mapped.MoveToImmutable(), token);
        lock (gate)
        {
            if (closed || token.IsCancellationRequested)
                return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Cancelled());
            if (thread is null)
            {
                thread = new Thread(Run) { Name = "Beets Man system playback keys", IsBackground = true };
                thread.Start();
            }
            pending.Add(request);
        }
        return request.Result.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!closed)
            {
                closed = true;
                pending.CompleteAdding();
                if (thread is null)
                {
                    pending.Dispose();
                    stopped.TrySetResult();
                }
            }
        }
        return new(stopped.Task);
    }

    private void Run()
    {
        var owned = new Dictionary<NativeHotkey, Registration>();
        ApplyRequest? current = null;
        try
        {
            while (!pending.IsCompleted)
            {
                if (pending.TryTake(out current, owned.Count == 0 ? Timeout.Infinite : 25))
                {
                    current.Result.TrySetResult(Replace(owned, current));
                    current = null;
                }
                for (var count = 0; owned.Count != 0 && count < 16 && api.TryReadActivation(out var id); count++)
                {
                    var active = owned.Values.FirstOrDefault(item => item.Id == id);
                    if (active is not null && !pending.IsAddingCompleted)
                        Activated?.Invoke(active.Binding);
                }
            }
        }
        catch (Exception error) when (IsNativeUnavailable(error))
        {
            var unavailable = new Outcome<bool>.DependencyFailure("system-playback-shortcuts-unavailable", false);
            current?.Result.TrySetResult(unavailable);
            lock (gate)
            {
                closed = true;
                pending.CompleteAdding();
            }
            while (pending.TryTake(out var queued))
                queued.Result.TrySetResult(unavailable);
        }
        finally
        {
            foreach (var item in owned.Values)
            {
                try { api.Unregister(item.Id); }
                catch (Exception error) when (IsNativeUnavailable(error)) { }
            }
            pending.Dispose();
            stopped.TrySetResult();
        }
    }

    private static bool IsNativeUnavailable(Exception error) =>
        error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private Outcome<bool> Replace(Dictionary<NativeHotkey, Registration> owned, ApplyRequest request)
    {
        var added = new List<Registration>(request.Bindings.Length);
        var desired = new Dictionary<NativeHotkey, Registration>();
        var accepted = false;
        try
        {
            foreach (var (key, binding) in request.Bindings)
            {
                if (request.Token.IsCancellationRequested || pending.IsAddingCompleted)
                    return new Outcome<bool>.Cancelled();
                if (owned.TryGetValue(key, out var retained))
                {
                    desired.Add(key, retained with { Binding = binding });
                    continue;
                }
                if (nextId >= 0xBFFF || !api.Register(++nextId, key))
                    return new Outcome<bool>.Success(false);
                var item = new Registration(nextId, binding);
                added.Add(item);
                desired.Add(key, item);
            }
            if (request.Token.IsCancellationRequested || pending.IsAddingCompleted)
                return new Outcome<bool>.Cancelled();
            foreach (var item in owned.Where(item => !desired.ContainsKey(item.Key)))
                api.Unregister(item.Value.Id);
            owned.Clear();
            foreach (var (key, item) in desired)
                owned.Add(key, item);
            accepted = true;
            return new Outcome<bool>.Success(true);
        }
        finally
        {
            if (!accepted)
                foreach (var item in added)
                    api.Unregister(item.Id);
        }
    }

    private sealed record Registration(int Id, ShortcutBinding Binding);

    private sealed record ApplyRequest(ImmutableArray<(NativeHotkey Hotkey, ShortcutBinding Binding)> Bindings, CancellationToken Token)
    {
        internal TaskCompletionSource<Outcome<bool>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Defines the user32 key and modifier pair without depending on Avalonia input.</summary>
/// <remarks>2026-11-25: Map captured gestures to native values for TEST-SETTINGS-002.</remarks>
internal readonly record struct NativeHotkey(uint Key, uint Modifiers)
{
    private static readonly IReadOnlyDictionary<string, uint> NamedKeys = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["MediaNextTrack"] = 0xB0, ["MediaPreviousTrack"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
        ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE
    };

    internal static NativeHotkey? FromGesture(string input)
    {
        if (ShortcutCatalog.NormalizeGesture(input) is not { } gesture)
            return null;
        var parts = gesture.Split('+');
        uint modifiers = 0x4000;
        foreach (var modifier in parts.AsSpan(0, parts.Length - 1))
        {
            modifiers |= modifier switch
            {
                "Alt" => 0x0001u, "Ctrl" => 0x0002u, "Shift" => 0x0004u, "Meta" => 0x0008u, _ => 0u
            };
        }
        var key = parts[^1];
        if (key.Length == 1)
            return new(key[0], modifiers);
        if (key == "F12")
            return null;
        if (key[0] == 'F' && uint.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function))
            return new(0x6F + function, modifiers);
        return NamedKeys.TryGetValue(key, out var virtualKey) ? new(virtualKey, modifiers) : null;
    }
}

/// <summary>Isolates the thread-owned operations of the Win32 message queue.</summary>
internal interface IWin32ShortcutApi
{
    bool Register(int id, NativeHotkey key);
    void Unregister(int id);
    bool TryReadActivation(out int id);
}

/// <summary>Uses thread messages and the native Windows hotkey API.</summary>
/// <remarks>2026-11-25: Bind the system playback port to user32 RegisterHotKey and WM_HOTKEY for TEST-SETTINGS-002.</remarks>
internal sealed class Win32ShortcutApi : IWin32ShortcutApi
{
    private const uint HotkeyMessage = 0x0312;
    private const uint RemoveMessage = 1;

    public bool Register(int id, NativeHotkey key) => RegisterHotKey(IntPtr.Zero, id, key.Modifiers, key.Key);
    public void Unregister(int id) => UnregisterHotKey(IntPtr.Zero, id);
    public bool TryReadActivation(out int id)
    {
        var found = PeekMessage(out var message, IntPtr.Zero, HotkeyMessage, HotkeyMessage, RemoveMessage);
        id = unchecked((int)message.WParam);
        return found;
    }

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal IntPtr Window;
        internal uint Kind;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }
}
