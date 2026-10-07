using RCSBackend.Modules.Rcs.Application.Tasks;
using RCSBackend.Modules.Rcs.Application.Vehicles;

namespace RCSBackend.Modules.Rcs.Application.Scheduling;

/// <summary>继续分配给其他空闲车，同时观察各车执行结果；停止宿主时等待所有车停止。</summary>
public sealed class RcsTaskDispatchWorker(IRcsTaskDispatchRuntime tasks, RcsVehicleRegistry vehicles) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            tasks.SetExecutionLifetime(lifetime.Token);
            await vehicles.InitializeAsync(lifetime.Token);
            await tasks.RecoverAsync(lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                await tasks.ObserveExecutionsAsync();
                if (!await tasks.DispatchNextAsync(lifetime.Token)) await tasks.WaitForWorkAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            await tasks.ObserveExecutionsAsync(drain: true);
        }
    }
}
