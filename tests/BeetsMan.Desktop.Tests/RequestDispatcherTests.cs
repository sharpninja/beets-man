using BeetsMan.Desktop.Application.Dispatch;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Runs the proven dispatch assertions against the production routing table.</summary>
/// <remarks>2026-11-24: Bind the mock-tested identity and cancellation checks to FR-DISPATCH-001#ac-routing.</remarks>
public sealed class RequestDispatcherTests
{
    [Fact]
    public async Task RoutingAndCancellationMatchTheProvenFixture()
    {
        var request = new DispatchProbe(8);
        var context = new CommandContext(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var handler = new DispatchProbeHandler();
        var dispatcher = new RequestDispatcher.Builder().RegisterCommand(handler).Build(typeof(DispatchProbe));
        var outcome = await dispatcher.SendAsync(request, context, CancellationToken.None);
        DispatchProbeAssertions.Verify(request, context, outcome, handler);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellation = await dispatcher.SendAsync(request, context, canceled.Token);
        DispatchProbeAssertions.VerifyCanceled(cancellation, handler);
    }

    [Fact]
    public async Task ReturnsTypedQueryResultsAndRejectsInvalidComposition()
    {
        var builder = new RequestDispatcher.Builder();
        Assert.Throws<InvalidOperationException>(() => builder.Build(typeof(ReadProbe)));
        builder.RegisterQuery(new ReadHandler());
        Assert.Throws<InvalidOperationException>(() => builder.RegisterQuery(new ReadHandler()));
        var dispatcher = builder.Build(typeof(ReadProbe));
        var context = new RequestContext(Guid.NewGuid(), null);
        var outcome = await dispatcher.QueryAsync(new ReadProbe(-5), context, CancellationToken.None);
        Assert.Equal("negative-value", Assert.Single(Assert.IsType<Outcome<int>.Invalid>(outcome).Issues).Code);
    }

    [Fact]
    public async Task CancellationAfterAnAcceptedHandlerResultPreservesTheReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new RequestDispatcher.Builder().RegisterCommand(new AcceptedHandler(cancellation)).Build(typeof(DispatchProbe));
        var result = await dispatcher.SendAsync(new DispatchProbe(17), new(new(Guid.NewGuid(), null), Guid.NewGuid()), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(17, Assert.IsType<Outcome<int>.Success>(result).Value);
    }

    private sealed record ReadProbe(int Value) : IQuery<int>;

    private sealed class ReadHandler : IQueryHandler<ReadProbe, int>
    {
        public Task<Outcome<int>> HandleAsync(ReadProbe query, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult<Outcome<int>>(query.Value < 0 ? new Outcome<int>.Invalid([new("negative-value", nameof(query.Value))]) :
                new Outcome<int>.Success(query.Value));
    }

    private sealed class AcceptedHandler(CancellationTokenSource caller) : ICommandHandler<DispatchProbe, int>
    {
        public Task<Outcome<int>> HandleAsync(DispatchProbe command, CommandContext context, CancellationToken cancellationToken)
        {
            caller.Cancel();
            return Task.FromResult<Outcome<int>>(new Outcome<int>.Success(command.Value));
        }
    }
}
