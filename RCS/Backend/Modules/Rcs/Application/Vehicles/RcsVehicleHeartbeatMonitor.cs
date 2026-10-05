namespace RCSBackend.Modules.Rcs.Application.Vehicles;

/// <summary>Marks heartbeat-based vehicles offline when telemetry stops arriving.</summary>
public sealed class RcsVehicleHeartbeatMonitor(
    RcsVehicleRegistry registry,
    ILogger<RcsVehicleHeartbeatMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await registry.InitializeAsync(stoppingToken);
                registry.ExpireHeartbeats();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "检查车辆心跳超时失败。");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
