using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises the STB09 desktop registration lifecycle through its host boundary.</summary>
/// <remarks>2026-11-25: Trace enrollment, authorization, and revocation to FR-RECORDS-001#ac-register and TEST-RECORDS-001.</remarks>
internal static class DesktopRegistrationAssertions
{
    internal enum Step
    {
        OperatorPending, ListenerCannotPublishBeforeEnrollment, RegisterOperator, StaleListenerEnrollment, ListenerPending,
        RegisterListener, ReplayEnrollment, DifferentEnrollmentIntent, ActiveRoster, ListenerPublishes, ListenerCannotRevoke,
        StaleRevocation, RevokeListener, ReplayRevocation, RevokedCannotReadRoster, RevokedCannotReadFeed,
        RevokedCannotPublish, RevokedCannotEnroll, OperatorRosterAfterRevocation
    }

    internal sealed record Response(int Status, long? Revision = null, string Detail = "");

    internal static async Task VerifyAsync(Func<Step, Task<Response>> send)
    {
        await Check(Step.OperatorPending, 200, 0);
        await Check(Step.ListenerCannotPublishBeforeEnrollment, 403);
        await Check(Step.RegisterOperator, 200, 1, "Studio desktop:Operator:Active");
        await Check(Step.StaleListenerEnrollment, 409, 1, "stale-instance-registry");
        await Check(Step.ListenerPending, 200, 1);
        await Check(Step.RegisterListener, 200, 2, "Travel laptop:Listener:Active");
        await Check(Step.ReplayEnrollment, 200, 2, "Travel laptop:Listener:Active");
        await Check(Step.DifferentEnrollmentIntent, 409, detail: "operation-intent-mismatch");
        await Check(Step.ActiveRoster, 200, 2, "Studio desktop:Operator:Active|Travel laptop:Listener:Active");
        await Check(Step.ListenerPublishes, 200);
        await Check(Step.ListenerCannotRevoke, 403);
        await Check(Step.StaleRevocation, 409, 2, "stale-instance-registry");
        const string revoked = "Studio desktop:Operator:Active|Travel laptop:Listener:Revoked";
        await Check(Step.RevokeListener, 200, 3, revoked);
        await Check(Step.ReplayRevocation, 200, 3, revoked);
        await Check(Step.RevokedCannotReadRoster, 403);
        await Check(Step.RevokedCannotReadFeed, 403);
        await Check(Step.RevokedCannotPublish, 403);
        await Check(Step.RevokedCannotEnroll, 403);
        await Check(Step.OperatorRosterAfterRevocation, 200, 3, revoked);

        async Task Check(Step step, int status, long? revision = null, string detail = "") =>
            Assert.Equal(new Response(status, revision, detail), await send(step));
    }
}
