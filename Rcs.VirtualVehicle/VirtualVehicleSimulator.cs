using Rcs.Contracts.Map;
using Rcs.Contracts.Vehicles;

namespace Rcs.VirtualVehicle;

/// <summary>单车、逐格移动的模拟实现；未来可替换成真实车辆通讯适配器。</summary>
public sealed class VirtualVehicleSimulator : IVirtualVehicle
{
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private bool _paused;
    private GridPoint _position;
    private int _routeIndex;
    private int _routeLength;
    private string _status = "Idle";

    public VirtualVehicleSimulator(GridPoint initialPosition) => _position = initialPosition;

    public VehicleStateDto State => new()
    {
        Position = _position,
        Status = _status,
        RouteIndex = _routeIndex,
        RouteLength = _routeLength
    };

    public event Action<VehicleStateDto>? StateChanged;

    public async Task RunAsync(IReadOnlyList<GridPoint> route, CancellationToken cancellationToken = default)
    {
        _runCts?.Cancel();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _runCts.Token;
        _paused = false;
        _routeLength = route.Count;
        _routeIndex = 0;
        _status = route.Count == 0 ? "Idle" : "Running";
        Publish();

        try
        {
            for (var i = 0; i < route.Count; i++)
            {
                while (_paused) await Task.Delay(100, token);
                token.ThrowIfCancellationRequested();
                _position = route[i];
                _routeIndex = i + 1;
                Publish();
                await Task.Delay(300, token);
            }
            _status = "Arrived";
            Publish();
        }
        catch (OperationCanceledException)
        {
            if (_status != "Paused") _status = "Idle";
            Publish();
        }
    }

    public void Pause()
    {
        _paused = true;
        _status = "Paused";
        Publish();
    }

    public void Reset(GridPoint position)
    {
        _runCts?.Cancel();
        _paused = false;
        _position = position;
        _routeIndex = 0;
        _routeLength = 0;
        _status = "Idle";
        Publish();
    }

    private void Publish() => StateChanged?.Invoke(State);
}
