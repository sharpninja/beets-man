using System.Net;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.RemoteControl.Protocol.V1;
using Avalonia.RemoteControl.Server;
using Avalonia.RemoteControl.Server.Hosting;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Presentation;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>
/// 2026-11-24: Verifies the test driver for TEST-DISPATCH-002 and FR-DISPATCH-001#ac-routing.
/// Exercises the action path through Avalonia and a Toolkit command with low-level input disabled.
/// </summary>
/// <remarks>2026-11-25: Exercise the cursor binding for FR-DISPATCH-001#ac-wait-pointer and TEST-DISPATCH-002.</remarks>
public sealed class RemoteControlIntegrationTests
{
    /// <summary>Discovers a headless button by automation ID and observes its Toolkit command update through gRPC.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvokesToolkitCommandWithoutKeyboardOrMouse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(RemoteControlTestApp));
        await session.Dispatch(async () =>
        {
            await RunFixtureAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Verifies that the blocking command owns the wait cursor until its operation or scope ends.</summary>
    /// <remarks>2026-11-25: Exercise actual cursor values and an independent control for FR-DISPATCH-001#ac-wait-pointer.</remarks>
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("replace owner")]
    [Trait("Category", "Integration")]
    public async Task BlockingCommandOwnsWaitCursor(string ending)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(RemoteControlTestApp));
        await session.Dispatch(async () =>
        {
            await RunWaitCursorFixtureAsync(ending, cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Asserts how a scoped one-way cursor binding follows the bootstrap command and releases the override.</summary>
    /// <remarks>2026-11-25: Bind the production ViewModel and cursor converter in the TEST-DISPATCH-002 fixture.</remarks>
    private static async Task RunWaitCursorFixtureAsync(string ending, CancellationToken cancellationToken)
    {
        const string retryId = "Fixture.Retry";
        const string independentId = "Fixture.Independent";
        var handler = new PendingBootstrap();
        var dispatcher = new RequestDispatcher.Builder().RegisterCommand<RetryDesktopBootstrap, BootstrapState>(handler).Build();
        using var viewModel = new BootstrapViewModel(new(dispatcher, new AvaloniaUiScheduler()), Guid.NewGuid());
        using var converter = new WaitCursorConverter();
        var retry = new Button { Content = "Retry", Command = viewModel.RetryCommand };
        AutomationProperties.SetAutomationId(retry, retryId);
        var scope = new ContentControl { Content = retry };
        using var cursorBinding = scope.Bind(InputElement.CursorProperty, new Binding(nameof(viewModel.IsInteractionBlocked))
        {
            Source = viewModel,
            Converter = converter,
            Mode = BindingMode.OneWay
        });
        var independentCalls = 0;
        var independent = new Button { Content = "Independent", Command = new RelayCommand(() => independentCalls++) };
        AutomationProperties.SetAutomationId(independent, independentId);
        var window = new Window { Content = new StackPanel { Children = { scope, independent } }, Width = 320, Height = 180 };

        await WithRemoteWindowAsync(window, async (client, headers, deadline) =>
        {
            Assert.Null(scope.Cursor);
            await InvokeEnabledButtonAsync(retryId);
            var operation = Assert.IsAssignableFrom<Task>(viewModel.RetryCommand.ExecutionTask);
            try
            {
                Assert.False(operation.IsCompleted);
                Assert.Equal(nameof(StandardCursorType.Wait), Assert.IsType<Cursor>(scope.Cursor).ToString());
                Assert.Null(independent.Cursor);
                await InvokeEnabledButtonAsync(independentId);
                Assert.Equal(1, independentCalls);
                Assert.False(operation.IsCompleted);

                switch (ending)
                {
                    case "failure":
                        handler.Reply.SetResult(new Outcome<BootstrapState>.DependencyFailure("database-unavailable", true));
                        break;
                    case "cancel":
                        viewModel.RetryCommand.Cancel();
                        break;
                    case "replace owner":
                        viewModel.Activate(Guid.NewGuid());
                        Assert.Null(scope.Cursor);
                        break;
                    default:
                        handler.Reply.SetResult(new Outcome<BootstrapState>.Success(new(1, BootstrapStatus.Ready, null, null)));
                        break;
                }
                await operation.WaitAsync(cancellationToken);
                Assert.Null(scope.Cursor);
            }
            finally
            {
                handler.Reply.TrySetResult(new Outcome<BootstrapState>.Cancelled());
            }

            async Task InvokeEnabledButtonAsync(string id)
            {
                var snapshot = await client.GetSnapshotAsync(new GetSnapshotRequest(), headers, deadline, cancellationToken);
                var button = Assert.Single(snapshot.Nodes, node => node.AutomationId == id);
                Assert.True(button.IsVisible);
                Assert.True(button.IsEnabled);
                var click = await client.InvokeClickAsync(new InvokeClickRequest { NodeId = button.Id }, headers, deadline, cancellationToken);
                Assert.True(click.Succeeded, click.Message);
            }
        }, cancellationToken);
    }

    /// <summary>Runs the input-free action smoke check using the common RemoteControl test host.</summary>
    /// <remarks>2026-11-25: Reuse authenticated loopback setup in the TEST-DISPATCH-002 checks.</remarks>
    private static async Task RunFixtureAsync(CancellationToken cancellationToken)
    {
        const string buttonId = "Fixture.RunCommand";
        var calls = 0;
        var control = new Button { Content = "Run command" };
        AutomationProperties.SetAutomationId(control, buttonId);
        AutomationProperties.SetName(control, "Ready");
        control.Command = new RelayCommand(() =>
        {
            calls++;
            AutomationProperties.SetName(control, "Command ran");
        });
        var window = new Window { Content = control, Width = 320, Height = 180 };
        await WithRemoteWindowAsync(window, async (client, headers, deadline) =>
        {
            var rejected = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.GetSnapshotAsync(new GetSnapshotRequest(), deadline: deadline, cancellationToken: cancellationToken));
            Assert.Equal(StatusCode.Unauthenticated, rejected.StatusCode);

            var capabilities = await client.GetCapabilitiesAsync(new GetCapabilitiesRequest(), headers, deadline, cancellationToken);
            Assert.True(capabilities.SupportsClickInvocation);
            Assert.False(capabilities.SupportsRemoteInput);

            var before = await client.GetSnapshotAsync(new GetSnapshotRequest(), headers, deadline, cancellationToken);
            var button = Assert.Single(before.Nodes, node => node.AutomationId == buttonId);
            Assert.Equal("Ready", button.AutomationName);
            Assert.True(button.IsVisible);
            Assert.True(button.IsEnabled);
            Assert.Equal(0, calls);

            var rawInput = new SendInputRequest();
            rawInput.Events.Add(new RemoteInputEvent { Text = "raw input is disabled" });
            var input = await client.SendInputAsync(rawInput, headers, deadline, cancellationToken);
            Assert.False(input.Succeeded);

            var click = await client.InvokeClickAsync(new InvokeClickRequest { NodeId = button.Id }, headers, deadline, cancellationToken);
            Assert.True(click.Succeeded, click.Message);
            var after = await client.GetSnapshotAsync(new GetSnapshotRequest(), headers, deadline, cancellationToken);
            Assert.Equal("Command ran", Assert.Single(after.Nodes, node => node.AutomationId == buttonId).AutomationName);
            Assert.Equal(1, calls);
        }, cancellationToken);
    }

    /// <summary>Hosts a test window and authenticated, action-only listener on one Avalonia dispatcher frame.</summary>
    /// <remarks>2026-11-25: Keep the TEST-DISPATCH-002 fixture display-free and isolate the gRPC server to loopback.</remarks>
    private static async Task WithRemoteWindowAsync(Window window,
        Func<RemoteControl.RemoteControlClient, Metadata, DateTime, Task> run, CancellationToken cancellationToken)
    {
        window.Show();
        try
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var services = new ServiceCollection();
            services.AddAvaloniaRemoteControl(options =>
            {
                options.IsEnabled = true;
                options.Host = IPAddress.Loopback;
                options.Port = 0;
                options.AuthenticationToken = token;
                options.AuthenticatedClientIdentity = "beets-man-integration-test";
                options.AllowRemoteActions = true;
                options.AllowRemoteInput = false;
            });
            services.AddSingleton<IRemoteControlRootProvider>(new TestRoot(window));
            await using var provider = services.BuildServiceProvider();
            var host = provider.GetRequiredService<AvaloniaRemoteControlServerHost>();
            await host.StartAsync(cancellationToken);
            var address = Assert.IsType<Uri>(host.BoundAddress);
            Assert.True(address.IsLoopback);
            Assert.True(address.Port > 0);

            using var channel = GrpcChannel.ForAddress(address);
            var client = new RemoteControl.RemoteControlClient(channel);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var headers = new Metadata { { "authorization", $"Bearer {token}" } };
            await run(client, headers, deadline);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Holds bootstrap until the fixture supplies the result or ends the command's lifetime.</summary>
    /// <remarks>2026-11-25: Control logical blocking deterministically for FR-DISPATCH-001#ac-wait-pointer.</remarks>
    private sealed class PendingBootstrap : ICommandHandler<RetryDesktopBootstrap, BootstrapState>
    {
        public TaskCompletionSource<Outcome<BootstrapState>> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Outcome<BootstrapState>> HandleAsync(RetryDesktopBootstrap command, CommandContext context, CancellationToken token) =>
            Reply.Task.WaitAsync(token);
    }

    /// <summary>Exposes just the test window to the RemoteControl server.</summary>
    private sealed class TestRoot(Control window) : IRemoteControlRootProvider
    {
        public Control GetRootControl() => window;
    }
}

/// <summary>2026-11-24: Supplies a display-free Avalonia application for the TEST-DISPATCH-002 driver check.</summary>
public sealed class RemoteControlTestApp : Avalonia.Application
{
    /// <summary>Creates the headless application on Avalonia's test dispatcher thread.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<RemoteControlTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
