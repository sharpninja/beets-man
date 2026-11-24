using Avalonia.Threading;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Applies application results on Avalonia's UI thread.</summary>
/// <remarks>2026-11-24: Implement FR-DISPATCH-001#ac-update without creating any screens.</remarks>
public sealed class AvaloniaUiScheduler : IUiScheduler
{
    /// <inheritdoc />
    public async Task PostAsync(Action apply, CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(apply, DispatcherPriority.Default, cancellationToken);
}
