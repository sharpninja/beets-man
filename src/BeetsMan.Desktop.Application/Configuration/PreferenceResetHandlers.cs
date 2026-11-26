using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Reviews local category overrides and confirms the same local and inherited revisions.</summary>
/// <remarks>2026-11-25: Bind the STB07 before-and-after review and conditional commit for FR-SETTINGS-001.</remarks>
/// <remarks>2026-11-26: Preserve scoped inheritance and validated resets for FR-SETTINGS-001#ac-precedence and FR-SETTINGS-001#ac-availability.</remarks>
public sealed partial class PlaybackPreferenceHandlers : IQueryHandler<GetPreferenceResetPlan, PreferenceResetPlan>,
    ICommandHandler<ResetLocalPreferences, PreferenceCategorySnapshot>
{
    private readonly SemaphoreSlim resetLane = new(1, 1);
    private readonly Dictionary<string, ResetReview> reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletedReset> completedResets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<Outcome<PreferenceResetPlan>> HandleAsync(GetPreferenceResetPlan query, RequestContext context, CancellationToken token)
    {
        if (PreferenceRegistry.Category(query.CategoryId).IsEmpty)
            return Invalid<PreferenceResetPlan>("unknown-preference-category");
        try
        {
            await resetLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var stored = await store.ReadPreferencesAsync(library, query.CategoryId, token).ConfigureAwait(false);
                return Project(stored, current =>
                {
                    var overridden = current.Preferences.Where(item => item.Local is not null).ToImmutableArray();
                    var plan = new PreferenceResetPlan(Guid.NewGuid(), query.CategoryId, current.Revision,
                        [.. overridden.Select(item => Effective(item with { Local = null }, current.Revision))],
                        [.. overridden.Select(item => Effective(item, current.Revision))], current.SharedRevision);
                    reviews[query.CategoryId] = new(context.OriginInstanceId, plan);
                    return plan;
                });
            }
            finally { resetLane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<PreferenceResetPlan>.Cancelled(); }
    }

    /// <inheritdoc />
    public async Task<Outcome<PreferenceCategorySnapshot>> HandleAsync(ResetLocalPreferences command, CommandContext context, CancellationToken token)
    {
        if (command.PlanId == Guid.Empty || command.ExpectedRevision < 0 || context.OperationId == Guid.Empty)
            return Invalid<PreferenceCategorySnapshot>("invalid-preference-reset");
        try
        {
            await resetLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var origin = context.Request.OriginInstanceId;
                if (completedResets.Values.FirstOrDefault(item => item.Operation == context.OperationId && item.Origin == origin) is { } previous)
                    return previous.Command == command ? new Outcome<PreferenceCategorySnapshot>.Success(previous.Receipt) :
                        new Outcome<PreferenceCategorySnapshot>.Conflict("preference-reset-operation-reused", previous.Receipt.Revision);
                if (reviews.Values.FirstOrDefault(item => item.Plan.PlanId == command.PlanId && item.Origin == origin) is not { } reviewed)
                    return Invalid<PreferenceCategorySnapshot>("unknown-preference-reset-plan");
                var plan = reviewed.Plan;
                if (plan.Revision != command.ExpectedRevision)
                    return new Outcome<PreferenceCategorySnapshot>.Conflict("stale-local-preferences", plan.Revision);
                var clears = plan.Inherited.ToImmutableDictionary(item => item.Key, _ => (PreferenceValue?)null, StringComparer.Ordinal);
                var saved = await store.WritePreferencesAsync(library, plan.CategoryId, plan.Revision, clears, token, plan.HostRevision)
                    .ConfigureAwait(false);
                var result = Project(saved, Category);
                if (result is Outcome<PreferenceCategorySnapshot>.Success confirmed)
                {
                    reviews.Remove(plan.CategoryId);
                    completedResets[plan.CategoryId] = new(origin, context.OperationId, command, confirmed.Value);
                }
                return result;
            }
            finally { resetLane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<PreferenceCategorySnapshot>.Cancelled(); }
    }

    private sealed record ResetReview(Guid? Origin, PreferenceResetPlan Plan);
    private sealed record CompletedReset(Guid? Origin, Guid Operation, ResetLocalPreferences Command, PreferenceCategorySnapshot Receipt);
}
