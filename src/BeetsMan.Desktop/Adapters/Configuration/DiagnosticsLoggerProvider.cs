using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Identifies a request and optionally a verified plugin in a diagnostic scope.</summary>
public sealed record DiagnosticScope(Guid? CorrelationId, string? PluginId = null);

/// <summary>Filters and records bounded event metadata for logs and the diagnostics query.</summary>
/// <remarks>2026-11-24: Implement FR-CONFIG-001#ac-logs and TR-CONFIG-ARCH-001 with a filtered provider and allow-listed event fields.</remarks>
public sealed class DiagnosticsLoggerProvider : ILoggerProvider, ISupportExternalScope, IQueryHandler<GetDiagnostics, DiagnosticsPage>
{
    private const int BufferSize = 1_024;
    private const int PageSize = 100;
    private readonly object gate = new();
    private readonly Queue<(long Position, DiagnosticEntry Entry)> entries = new(BufferSize);
    private readonly BootstrapSettings settings;
    private readonly TimeProvider clock;
    private readonly int eventsPerSecond;
    private readonly RollingDiagnosticsFile? rolling;
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    private long position;
    private long windowStart;
    private int eventsInWindow;
    private bool disposed;

    /// <summary>Creates diagnostics for the resolved logging settings.</summary>
    public DiagnosticsLoggerProvider(BootstrapSettings settings, TimeProvider? clock = null, int eventsPerSecond = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventsPerSecond);
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.eventsPerSecond = eventsPerSecond;
        if (settings.RollingLog is { Enabled: true } fileSettings)
            rolling = new(fileSettings);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => IsIdentifier(categoryName, 160)
        ? new CategoryLogger(this, categoryName, ResolveLevel(categoryName))
        : NullLogger.Instance;

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => Volatile.Write(ref scopes, scopeProvider);

    /// <inheritdoc />
    public Task<Outcome<DiagnosticsPage>> HandleAsync(GetDiagnostics query, RequestContext context, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult<Outcome<DiagnosticsPage>>(new Outcome<DiagnosticsPage>.Cancelled());

        var before = long.MaxValue;
        if (query.Page is { } cursor && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out before) || before < 1))
            return Task.FromResult<Outcome<DiagnosticsPage>>(new Outcome<DiagnosticsPage>.Invalid([new("invalid-diagnostics-page", nameof(query.Page))]));

        var filter = query.Filter;
        lock (gate)
        {
            var page = entries.Reverse().Where(item => item.Position < before && Matches(item.Entry, filter)).Take(PageSize + 1).ToArray();
            var nextPage = page.Length > PageSize ? page[PageSize - 1].Position.ToString(CultureInfo.InvariantCulture) : null;
            var result = new DiagnosticsPage(filter, page.Take(PageSize).Select(item => item.Entry).ToImmutableArray(), nextPage);
            return Task.FromResult<Outcome<DiagnosticsPage>>(new Outcome<DiagnosticsPage>.Success(result));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
            disposed = true;
    }

    private void Record(string category, LogLevel level, EventId eventId)
    {
        lock (gate)
        {
            if (disposed)
                return;
            var now = clock.GetTimestamp();
            if (eventsInWindow == 0 || clock.GetElapsedTime(windowStart, now) >= TimeSpan.FromSeconds(1))
            {
                windowStart = now;
                eventsInWindow = 0;
            }
            if (eventsInWindow >= eventsPerSecond)
                return;
            eventsInWindow++;

            var scope = new ActiveScope();
            Volatile.Read(ref scopes).ForEachScope(static (value, active) =>
            {
                if (value is DiagnosticScope known)
                {
                    active.CorrelationId = known.CorrelationId ?? active.CorrelationId;
                    if (known.PluginId is { } id && IsIdentifier(id, 96))
                        active.PluginId = id;
                }
            }, scope);
            var entry = new DiagnosticEntry(clock.GetUtcNow(), level.ToString(), category,
                eventId.Id.ToString(CultureInfo.InvariantCulture), scope.CorrelationId, scope.PluginId);
            if (entries.Count == BufferSize)
                entries.Dequeue();
            entries.Enqueue((++position, entry));
            rolling?.Append(entry);
        }
    }

    private LogLevel ResolveLevel(string category)
    {
        var bestLength = 0;
        var level = LogLevel.Information;
        foreach (var (key, value) in settings.LogLevels)
        {
            if (!Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                continue;
            if (key.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                if (bestLength == 0)
                    level = parsed;
            }
            else if (key.Length > bestLength && MatchesCategory(category, key))
            {
                level = parsed;
                bestLength = key.Length;
            }
        }
        return level;
    }

    private static bool Matches(DiagnosticEntry entry, DiagnosticsFilter filter) =>
        (string.IsNullOrEmpty(filter.Category) || MatchesCategory(entry.Category, filter.Category)) &&
        (filter.CorrelationId is null || filter.CorrelationId == entry.CorrelationId) &&
        (filter.PluginId is null || string.Equals(filter.PluginId, entry.PluginId, StringComparison.Ordinal));

    private static bool MatchesCategory(string category, string prefix) =>
        category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (category.Length == prefix.Length || category[prefix.Length] == '.');

    private static bool IsIdentifier(string identifier, int length) =>
        identifier.Length > 0 && identifier.Length <= length &&
        identifier.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+' or '`');

    private sealed class ActiveScope
    {
        public Guid? CorrelationId { get; set; }
        public string? PluginId { get; set; }
    }

    private sealed class CategoryLogger(DiagnosticsLoggerProvider owner, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Volatile.Read(ref owner.scopes).Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                owner.Record(category, logLevel, eventId);
        }
    }
}
