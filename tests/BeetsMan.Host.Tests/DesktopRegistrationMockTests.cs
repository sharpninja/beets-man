using Xunit;
using static BeetsMan.Host.Tests.DesktopRegistrationAssertions;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies that the independent registration harness detects wrong state or access.</summary>
/// <remarks>2026-11-25: Exercise deliberate wrong outcomes for TEST-RECORDS-001 and the STB09 roles.</remarks>
/// <remarks>2026-11-26: Reject unmanaged or incorrectly revoked registrations for FR-RECORDS-001#ac-register.</remarks>
public sealed class DesktopRegistrationMockTests
{
    [Fact]
    public async Task Register_MatchesContract()
    {
        var responses = new Dictionary<Step, Response>
        {
            [Step.OperatorPending] = new(200, 0),
            [Step.ListenerCannotPublishBeforeEnrollment] = new(403),
            [Step.RegisterOperator] = new(200, 1, "Studio desktop:Operator:Active"),
            [Step.StaleListenerEnrollment] = new(409, 1, "stale-instance-registry"),
            [Step.ListenerPending] = new(200, 1),
            [Step.RegisterListener] = new(200, 2, "Travel laptop:Listener:Active"),
            [Step.ReplayEnrollment] = new(200, 2, "Travel laptop:Listener:Active"),
            [Step.DifferentEnrollmentIntent] = new(409, Detail: "operation-intent-mismatch"),
            [Step.ActiveRoster] = new(200, 2, "Studio desktop:Operator:Active|Travel laptop:Listener:Active"),
            [Step.ListenerPublishes] = new(200),
            [Step.ListenerCannotRevoke] = new(403),
            [Step.StaleRevocation] = new(409, 2, "stale-instance-registry"),
            [Step.RevokeListener] = new(200, 3, "Studio desktop:Operator:Active|Travel laptop:Listener:Revoked"),
            [Step.ReplayRevocation] = new(200, 3, "Studio desktop:Operator:Active|Travel laptop:Listener:Revoked"),
            [Step.RevokedCannotReadRoster] = new(403),
            [Step.RevokedCannotReadFeed] = new(403),
            [Step.RevokedCannotPublish] = new(403),
            [Step.RevokedCannotEnroll] = new(403),
            [Step.OperatorRosterAfterRevocation] = new(200, 3, "Studio desktop:Operator:Active|Travel laptop:Listener:Revoked")
        };
        await VerifyAsync(step => Task.FromResult(responses[step]));
        foreach (var (wrongStep, correct) in responses)
        {
            var wrong = correct.Revision.HasValue ? correct with { Revision = correct.Revision + 1 } :
                correct with { Status = correct.Status == 403 ? 200 : 403 };
            await Assert.ThrowsAnyAsync<Exception>(() => VerifyAsync(step => Task.FromResult(step == wrongStep ? wrong : responses[step])));
        }
        await Assert.ThrowsAnyAsync<Exception>(() => VerifyAsync(step => Task.FromResult(step == Step.RegisterListener
            ? new(200, 2, "Travel laptop:Operator:Active") : responses[step])));
        await Assert.ThrowsAnyAsync<Exception>(() => VerifyAsync(step => Task.FromResult(step == Step.ListenerPending
            ? new(200, 1, "Studio desktop:Operator:Active") : responses[step])));
    }
}
