using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using Tmds.DBus.Protocol;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Owns an opt-in Linux XDG GlobalShortcuts portal session.</summary>
/// <remarks>2026-11-25: Bind FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002 without a window or direct input capture.</remarks>
internal sealed class XdgShortcutPlatform(string? address) : ISystemPlaybackShortcutPlatform
{
    private PortalShortcutSession? active;

    public event Action<ShortcutBinding>? Activated;
    public event Action? RegistrationChanged;

    /// <inheritdoc />
    public ImmutableArray<ShortcutBinding>? ActiveBindings => Volatile.Read(ref active)?.Bindings ?? [];

    /// <inheritdoc />
    public async Task<Outcome<bool>> ApplyAsync(ImmutableArray<ShortcutBinding> bindings, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return new Outcome<bool>.Cancelled();
        if (bindings.IsEmpty)
        {
            if (Interlocked.Exchange(ref active, null) is { } previous)
                await previous.DisposeAsync().ConfigureAwait(false);
            return new Outcome<bool>.Success(true);
        }
        if (string.IsNullOrEmpty(address))
            return new Outcome<bool>.DependencyFailure("system-playback-shortcuts-unavailable", false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        PortalShortcutSession? candidate = null;
        try
        {
            candidate = new(address, bindings, Deliver, ReportChange);
            if (!await candidate.BindAsync(deadline.Token).ConfigureAwait(false))
                return new Outcome<bool>.Success(false);
            deadline.Token.ThrowIfCancellationRequested();
            var old = Interlocked.Exchange(ref active, candidate);
            candidate = null;
            if (old is not null)
                await old.DisposeAsync().ConfigureAwait(false);
            return new Outcome<bool>.Success(true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return token.IsCancellationRequested ? new Outcome<bool>.Cancelled() :
                new Outcome<bool>.DependencyFailure("system-playback-shortcuts-timeout", false);
        }
        catch (DBusErrorReplyException error) when (error.ErrorName.EndsWith(".NotAllowed", StringComparison.Ordinal) ||
            error.ErrorName.EndsWith(".AccessDenied", StringComparison.Ordinal) || error.ErrorName.EndsWith(".Cancelled", StringComparison.Ordinal))
        {
            return new Outcome<bool>.Success(false);
        }
        catch (InvalidDataException)
        {
            return new Outcome<bool>.DependencyFailure("system-playback-shortcuts-protocol-invalid", false);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or System.Net.Sockets.SocketException or ArgumentException)
        {
            return new Outcome<bool>.DependencyFailure("system-playback-shortcuts-unavailable", false);
        }
        finally
        {
            if (candidate is not null)
                await candidate.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref active, null) is { } previous)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    private void Deliver(PortalShortcutSession sender, ShortcutBinding binding)
    {
        if (ReferenceEquals(sender, Volatile.Read(ref active)))
            Activated?.Invoke(binding);
    }

    private void ReportChange(PortalShortcutSession sender)
    {
        if (ReferenceEquals(sender, Volatile.Read(ref active)))
            RegistrationChanged?.Invoke();
    }
}

/// <summary>Maps normalized gestures to the XDG shortcuts specification's preferred triggers.</summary>
internal static class XdgShortcutGesture
{
    /// <summary>Uses XKB key names and platform modifier identifiers for FR-SETTINGS-001#ac-shortcuts.</summary>
    internal static string Format(string gesture)
    {
        var tokens = gesture.Split('+');
        tokens[^1] = tokens[^1] switch
        {
            "Space" => "space", "Esc" => "Escape", "Enter" => "Return", "Backspace" => "BackSpace",
            "PageUp" => "Page_Up", "PageDown" => "Page_Down",
            "Plus" => "plus", "Minus" => "minus", "Comma" => "comma", "Period" => "period",
            "MediaPlayPause" => "XF86AudioPlay", "MediaNextTrack" => "XF86AudioNext",
            "MediaPreviousTrack" => "XF86AudioPrev", "MediaStop" => "XF86AudioStop",
            { Length: 1 } key => key.ToLowerInvariant(), var key => key
        };
        for (var index = 0; index < tokens.Length - 1; index++)
            tokens[index] = tokens[index] == "Meta" ? "LOGO" : tokens[index].ToUpperInvariant();
        return string.Join('+', tokens);
    }
}
