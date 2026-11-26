using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Identifies the playback preference category and its accepted typed values.</summary>
/// <remarks>2026-11-25: Keep restart opt-in local for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public static class PlaybackPreferences
{
    public const string Category = "playback";
    public const string ResumeAfterRestart = "playback.resumeAfterRestart";
    public const string AutomaticFadeDuration = SharedLibraryDefaults.AutomaticFadeDuration;

    /// <summary>Checks a registered local playback value. Null clears a local override.</summary>
    public static bool Accepts(string key, PreferenceValue? value) =>
        PreferenceRegistry.Find(key) is { Category: Category } definition && definition.Accepts(value);
}

/// <summary>Resolves settings through the local, shared, and built-in layers.</summary>
/// <remarks>2026-11-25: Implement the local restart toggle and shareable fade duration for FR-PLAYER-001 and FR-SETTINGS-001.</remarks>
/// <remarks>2026-11-25: Bind typed notification preferences and generic category resets for FR-SETTINGS-001.</remarks>
/// <remarks>2026-11-25: Replicate a shared Appearance default and preserve the inherited source for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed partial class PlaybackPreferenceHandlers(Guid library, IDesktopPreferenceStore store, ILibraryRecordHost? host = null) :
    IPlaybackRestartPreference, IPlaybackFadePreference, IQueryHandler<GetPreference, EffectivePreference>, IQueryHandler<GetPreferenceCategory, PreferenceCategorySnapshot>,
    ICommandHandler<SetLocalOverride, EffectivePreference>, ICommandHandler<ClearLocalOverride, EffectivePreference>,
    ICommandHandler<SetLocalPreferences, PreferenceCategorySnapshot>, ICommandHandler<SetSharedPreferences, PreferenceCategorySnapshot>
{
    /// <summary>Registers playback preference reads and writes for the normal desktop.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetPreference, EffectivePreference>(this).RegisterQuery<GetPreferenceCategory, PreferenceCategorySnapshot>(this)
        .RegisterCommand<SetLocalOverride, EffectivePreference>(this).RegisterCommand<ClearLocalOverride, EffectivePreference>(this)
        .RegisterCommand<SetLocalPreferences, PreferenceCategorySnapshot>(this).RegisterCommand<SetSharedPreferences, PreferenceCategorySnapshot>(this)
        .RegisterQuery<GetPreferenceResetPlan, PreferenceResetPlan>(this).RegisterCommand<ResetLocalPreferences, PreferenceCategorySnapshot>(this);

    /// <inheritdoc />
    public async Task<Outcome<bool>> ReadRestartPreferenceAsync(CancellationToken token) =>
        Project(await store.ReadPreferencesAsync(library, PlaybackPreferences.Category, token).ConfigureAwait(false),
            values => ((PreferenceValue.Flag)FindEffective(values, PlaybackPreferences.ResumeAfterRestart).Value).Value);

    /// <inheritdoc />
    public async Task<Outcome<TimeSpan>> ReadFadeDurationAsync(CancellationToken token) =>
        Project(await store.ReadPreferencesAsync(library, PlaybackPreferences.Category, token).ConfigureAwait(false),
            values => ((PreferenceValue.Duration)FindEffective(values, PlaybackPreferences.AutomaticFadeDuration).Value).Value);

    /// <inheritdoc />
    public async Task<Outcome<EffectivePreference>> HandleAsync(GetPreference query, RequestContext context, CancellationToken token) =>
        PreferenceRegistry.Find(query.Key) is { } definition
            ? Project(await store.ReadPreferencesAsync(library, definition.Category, token).ConfigureAwait(false), values => FindEffective(values, query.Key))
            : Invalid<EffectivePreference>("unknown-playback-preference");

    /// <inheritdoc />
    public async Task<Outcome<PreferenceCategorySnapshot>> HandleAsync(GetPreferenceCategory query, RequestContext context, CancellationToken token) =>
        !PreferenceRegistry.Category(query.CategoryId).IsEmpty
            ? Project(await store.ReadPreferencesAsync(library, query.CategoryId, token).ConfigureAwait(false), Category)
            : Invalid<PreferenceCategorySnapshot>("unknown-preference-category");

    /// <inheritdoc />
    public Task<Outcome<EffectivePreference>> HandleAsync(SetLocalOverride command, CommandContext context, CancellationToken token) =>
        command.Value is not null ? PatchOneAsync(command.Key, command.Value, command.ExpectedRevision, token) :
            Task.FromResult(Invalid<EffectivePreference>("invalid-playback-preference"));

    /// <inheritdoc />
    public Task<Outcome<EffectivePreference>> HandleAsync(ClearLocalOverride command, CommandContext context, CancellationToken token) =>
        PatchOneAsync(command.Key, null, command.ExpectedRevision, token);

    /// <inheritdoc />
    public async Task<Outcome<PreferenceCategorySnapshot>> HandleAsync(SetLocalPreferences command, CommandContext context, CancellationToken token)
    {
        if (command.ExpectedRevision < 0 || command.Changes is not { Count: > 0 } ||
            command.Changes.Any(pair => pair.Value is null ||
                PreferenceRegistry.Find(pair.Key) is not { } definition || definition.Category != command.CategoryId || !definition.Accepts(pair.Value)))
            return Invalid<PreferenceCategorySnapshot>("invalid-playback-preferences");
        var changed = command.Changes.ToImmutableDictionary(pair => pair.Key, pair => (PreferenceValue?)pair.Value, StringComparer.Ordinal);
        return Project(await store.WritePreferencesAsync(library, command.CategoryId, command.ExpectedRevision, changed, token).ConfigureAwait(false), Category);
    }

    /// <inheritdoc />
    public async Task<Outcome<PreferenceCategorySnapshot>> HandleAsync(SetSharedPreferences command, CommandContext context, CancellationToken token)
    {
        if (command.ExpectedHostRevision < 0 || context.OperationId == Guid.Empty || command.Changes is not { Count: 1 })
            return Invalid<PreferenceCategorySnapshot>("invalid-shared-playback-preference");
        var (key, value) = command.Changes.Single();
        if (value is null || PreferenceRegistry.Find(key) is not { Shareable: true } definition ||
            definition.Category != command.CategoryId || !definition.Accepts(value))
            return Invalid<PreferenceCategorySnapshot>("invalid-shared-playback-preference");
        if (host is null)
            return new Outcome<PreferenceCategorySnapshot>.DependencyFailure("shared-preference-host-unavailable", true);
        long? duration = value is PreferenceValue.Duration ticks ? ticks.Value.Ticks : null;
        bool? flag = value is PreferenceValue.Flag enabled ? enabled.Value : null;
        var text = (value as PreferenceValue.Text)?.Value;
        var submitted = await host.PublishAsync(new LibraryRecordIntent.SharedDefault(key, duration, command.ExpectedHostRevision, flag, text),
            context.OperationId, token).ConfigureAwait(false);
        if (submitted is not Outcome<LibraryRecordReceipt>.Success { Value: var receipt })
            return LibraryBatchHandlers.CopyFailure<LibraryRecordReceipt, PreferenceCategorySnapshot>(submitted);
        if (receipt.LibraryId != library || receipt.OperationId != context.OperationId || receipt.RecordRevision < 1 ||
            receipt.RecordId != LibraryRecordIdentity.SharedDefault(library, key))
            return new Outcome<PreferenceCategorySnapshot>.DependencyFailure("shared-preference-receipt-mismatch", true);
        var cached = await store.CacheSharedPreferenceAsync(library, receipt.RecordRevision,
            new(key, SharedLibraryDefaults.RegistryVersion, duration, flag, text), CancellationToken.None).ConfigureAwait(false);
        return Project(cached, Category);
    }

    private async Task<Outcome<EffectivePreference>> PatchOneAsync(string key, PreferenceValue? value, long expected, CancellationToken token)
    {
        if (PreferenceRegistry.Find(key) is not { } definition || !definition.Accepts(value) || expected < 0)
            return Invalid<EffectivePreference>("invalid-playback-preference");
        var patch = ImmutableDictionary<string, PreferenceValue?>.Empty.Add(key, value);
        return Project(await store.WritePreferencesAsync(library, definition.Category, expected, patch, token).ConfigureAwait(false),
            values => FindEffective(values, key));
    }

    private PreferenceCategorySnapshot Category(StoredPreferences values) => new(values.Category, values.Revision,
        values.SharedRevision, [.. values.Preferences.Select(item => Effective(item, values.Revision))]);

    private EffectivePreference FindEffective(StoredPreferences values, string key) =>
        Effective(values.Preferences.Single(item => item.Definition.Key == key), values.Revision);

    private EffectivePreference Effective(StoredPreference item, long revision) => new(item.Definition.Key,
        item.Local ?? item.Shared ?? item.Definition.BuiltIn,
        item.Local is not null ? PreferenceSource.LocalOverride : item.Shared is not null ? PreferenceSource.HostDefault : PreferenceSource.BuiltIn,
        revision, true, item.Definition.Shareable && host is not null) { SharedDefault = item.Shared };

    private static Outcome<T> Project<T>(Outcome<StoredPreferences> stored, Func<StoredPreferences, T> convert) =>
        stored is Outcome<StoredPreferences>.Success success ? new Outcome<T>.Success(convert(success.Value))
            : LibraryBatchHandlers.CopyFailure<StoredPreferences, T>(stored);

    private static Outcome<T> Invalid<T>(string code) => new Outcome<T>.Invalid([new(code)]);
}
