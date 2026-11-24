using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Converts logical interaction blocking into the platform's standard wait cursor.</summary>
/// <remarks>2026-11-24: Expose the wait pointer for logically blocked UI without making normal background jobs block interaction.</remarks>
public sealed class WaitCursorConverter : IValueConverter, IDisposable
{
    private Cursor? waitCursor;

    /// <summary>Returns the wait cursor when blocked and clears the override otherwise.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? waitCursor ??= new Cursor(StandardCursorType.Wait) : null;

    /// <summary>Rejects reverse conversion because this binding is one-way.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The wait cursor binding is one-way.");

    /// <summary>Releases the platform cursor owned by this converter.</summary>
    public void Dispose()
    {
        waitCursor?.Dispose();
        waitCursor = null;
    }
}
