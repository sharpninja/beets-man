using BeetsMan.Desktop.Application.Dispatch;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the typed dispatch assertions against a recording fake before adding the production dispatcher.</summary>
/// <remarks>2026-11-24: Add the mock gate for FR-DISPATCH-001#ac-routing and TEST-DISPATCH-001.</remarks>
public sealed class RequestDispatcherMockTests
{
    [Fact]
    public async Task OutcomeAndIdentityAssertionsDiscriminateIncorrectDispatch()
    {
        var request = new DispatchProbe(8);
        var context = new CommandContext(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var doubleHandler = new DispatchProbeHandler();
        var dispatcher = new IndependentDispatcher(doubleHandler);
        var reply = await dispatcher.SendAsync(request, context, CancellationToken.None);
        DispatchProbeAssertions.Verify(request, context, reply, doubleHandler);

        Assert.ThrowsAny<XunitException>(() => DispatchProbeAssertions.Verify(request, context,
            new Outcome<int>.Success(99), doubleHandler));
        Assert.ThrowsAny<XunitException>(() => DispatchProbeAssertions.Verify(request,
            context with { OperationId = Guid.NewGuid() }, reply, doubleHandler));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellation = await dispatcher.SendAsync(request, context, canceled.Token);
        DispatchProbeAssertions.VerifyCanceled(cancellation, doubleHandler);
        Assert.ThrowsAny<XunitException>(() => DispatchProbeAssertions.VerifyCanceled(reply, doubleHandler));
    }

    private sealed class IndependentDispatcher(DispatchProbeHandler handler) : IRequestDispatcher
    {
        public async Task<Outcome<TResult>> SendAsync<TResult>(ICommand<TResult> command, CommandContext context, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return new Outcome<TResult>.Cancelled();
            Assert.IsType<DispatchProbe>(command);
            var reply = await handler.HandleAsync((DispatchProbe)(object)command, context, token);
            return (Outcome<TResult>)(object)reply;
        }

        public Task<Outcome<TResult>> QueryAsync<TResult>(IQuery<TResult> query, RequestContext context, CancellationToken token) =>
            throw new InvalidOperationException("This mock fixture exercises command routing only.");
    }
}

internal sealed record DispatchProbe(int Value) : ICommand<int>;

internal sealed class DispatchProbeHandler : ICommandHandler<DispatchProbe, int>
{
    public int Calls { get; private set; }
    public DispatchProbe? Request { get; private set; }
    public CommandContext Context { get; private set; }

    public Task<Outcome<int>> HandleAsync(DispatchProbe command, CommandContext context, CancellationToken cancellationToken)
    {
        Calls++;
        Request = command;
        Context = context;
        return Task.FromResult<Outcome<int>>(new Outcome<int>.Success(command.Value + 3));
    }
}

internal static class DispatchProbeAssertions
{
    public static void Verify(DispatchProbe request, CommandContext context, Outcome<int> actual, DispatchProbeHandler handler)
    {
        Assert.Equal(1, handler.Calls);
        Assert.Same(request, handler.Request);
        Assert.Equal(context, handler.Context);
        Assert.Equal(11, Assert.IsType<Outcome<int>.Success>(actual).Value);
    }

    public static void VerifyCanceled(Outcome<int> actual, DispatchProbeHandler handler)
    {
        Assert.IsType<Outcome<int>.Cancelled>(actual);
        Assert.Equal(1, handler.Calls);
    }
}
