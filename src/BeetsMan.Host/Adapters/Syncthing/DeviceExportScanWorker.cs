using BeetsMan.Host.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Registers the sender dispatcher for a host with a scan store and sender registry.</summary>
/// <remarks>2026-11-24: Run the durable FR-FILESYNC-001#ac-safety scan queue at host startup.</remarks>
public static class DeviceExportScanServices
{
    /// <summary>Adds the worker and a sender HTTP transport with local routing and redirects disabled.</summary>
    public static IServiceCollection AddDeviceExportScanWorker(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<SyncthingHostExportScanner>().ConfigurePrimaryHttpMessageHandler(CreateTransport);
        services.TryAddSingleton<IDeviceExportSenderScanner>(provider => provider.GetRequiredService<SyncthingHostExportScanner>());
        services.TryAddSingleton<DeviceExportScanDispatcher>();
        services.AddHostedService<DeviceExportScanWorker>();
        return services;
    }

    /// <summary>Adds the receiver observer for a host supplying its transfer store and receiver credentials.</summary>
    public static IServiceCollection AddDeviceExportReceiverVerification(this IServiceCollection services)
    {
        services.AddDeviceExportScanWorker();
        services.AddHttpClient<SyncthingDeviceExportReceiverVerifier>().ConfigurePrimaryHttpMessageHandler(CreateTransport);
        services.TryAddSingleton<IDeviceExportReceiverVerifier>(provider => provider.GetRequiredService<SyncthingDeviceExportReceiverVerifier>());
        services.TryAddSingleton<DeviceExportReceiverDispatcher>();
        return services;
    }

    private static SocketsHttpHandler CreateTransport() => new() { AllowAutoRedirect = false, UseProxy = false };
}

/// <summary>Continues due work after the host restarts and pauses between empty queue checks.</summary>
internal sealed class DeviceExportScanWorker(DeviceExportScanDispatcher dispatcher, IEnumerable<DeviceExportReceiverDispatcher> receivers,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await dispatcher.RunNextAsync(stoppingToken).ConfigureAwait(false);
            var processed = result is HostOutcome<bool>.Success { Value: true };
            foreach (var receiver in receivers)
            {
                var observed = await receiver.RunNextAsync(stoppingToken).ConfigureAwait(false);
                processed |= observed is HostOutcome<bool>.Success { Value: true };
            }
            if (!processed)
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken).ConfigureAwait(false);
        }
    }
}
