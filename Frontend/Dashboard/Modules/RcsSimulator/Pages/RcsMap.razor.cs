using System.Globalization;
using System.Text;
using System.Text.Json;
using Contracts.Rcs.Inventory;
using Contracts.Rcs.Map;
using Contracts.Rcs.Route;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Vehicle;
using Dashboard.Modules.RcsSimulator.Components;
using Dashboard.Modules.RcsSimulator.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Dashboard.Modules.RcsSimulator.Pages;

public partial class RcsMap
{
    private const string HostId = "rcs-runtime-map-host";
    private RcsMapEditorDto? _editor;
    private IReadOnlyList<RcsInventoryInstanceDto> _inventoryInstances = [];
    private string _inventoryError = "";
    private RcsMapPointDto? _selectedPoint;
    private RcsMapLineDto? _selectedLine;
    private RouteDto _route = new();
    private readonly Dictionary<string, VehicleStateDto> _vehicles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RcsTaskDto> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedVehicleId = "";
    private VehicleStateDto? SelectedVehicle => _vehicles.GetValueOrDefault(_selectedVehicleId);
    private RcsTaskDto? SelectedVehicleTask => SelectedVehicle is { } vehicle
        ? _tasks.Values.Where(task => task.VehicleId == vehicle.Id
            && task.Status is RcsTaskStatus.Running or RcsTaskStatus.Paused or RcsTaskStatus.Cancelling)
            .OrderByDescending(task => task.CreatedAt, StringComparer.Ordinal).FirstOrDefault()
        : null;
    private IReadOnlyList<string> VisibleRoutePointCodes => SelectedVehicleTask is { RoutePointCodes.Count: > 0 } task
        ? task.RoutePointCodes : _route.Found ? _route.PointCodes : Array.Empty<string>();
    private static string FormatPath(IReadOnlyList<string>? codes) => codes is { Count: > 0 }
        ? string.Join(" → ", codes) : "当前没有任务路径";
    private bool _vehicleBusy, _pickMenuOpen, _focusPickMenu;
    private double _pickMenuX, _pickMenuY;
    private string[] _pickVehicleIds = [], _pickPointCodes = [];
    private ElementReference _pickMenu;
    private IEnumerable<VehicleStateDto> PickVehicles => _pickVehicleIds.Where(_vehicles.ContainsKey).Select(id => _vehicles[id]);
    private IEnumerable<RcsMapPointDto> PickPoints => _editor?.Points.Where(p => _pickPointCodes.Contains(p.PointCode)) ?? [];
    private string PickMenuStyle => FormattableString.Invariant($"left:{_pickMenuX}px;top:{_pickMenuY}px");
    private IEnumerable<RcsMapNode> VehiclePoints => EnabledPoints.Select(p => new RcsMapNode(p.PointCode, p.PointType, p.X, p.Y, p.Z));
    private RcsMoveTaskDialog? _moveTaskDialog;
    private bool _showMoveDialog;
    private string _moveStart = "", _moveEnd = "";
    private string? _startCode;
    private string? _endCode;
    private string _feedback = "";
    private bool _feedbackError, _dirty, _saving, _loading, _mounted, _canvasDirty = true, _mapCanvasDirty, _fitPending, _disposed;
    private DotNetObjectReference<RcsMap>? _canvasRef;
    private IEnumerable<RcsMapPointDto> EnabledPoints => _editor?.Points.Where(x => x.IsEnabled) ?? Enumerable.Empty<RcsMapPointDto>();
    private bool CanCalculate => !_dirty && !_saving && _editor is not null && !string.IsNullOrWhiteSpace(_startCode)
        && !string.IsNullOrWhiteSpace(_endCode) && _startCode != _endCode
        && EnabledPoints.Any(x => x.PointCode == _startCode) && EnabledPoints.Any(x => x.PointCode == _endCode);

    protected override async Task OnInitializedAsync()
    {
        try { var saved = await Js.InvokeAsync<string?>("localStorage.getItem", "rcs_backend_url"); if (!string.IsNullOrWhiteSpace(saved)) Api.SetBaseUrl(saved); } catch { }
        await ReloadAsync();
        Realtime.VehicleStateChanged += OnVehicleStateChanged;
        Realtime.VehiclesChanged += OnVehiclesChanged;
        Realtime.TaskChanged += OnTaskChanged;
        Realtime.TasksCleared += OnTasksCleared;
        Realtime.InventoryChanged += OnInventoryChangedRealtime;
        Realtime.Reconnected += OnReconnected;
        await RefreshVehiclesAsync();
        await RefreshTasksAsync();
        Realtime.SetBaseUrl(Api.BaseUrl);
        try { await Realtime.StartAsync(); } catch { }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var canvasDirty = _canvasDirty;
        _canvasDirty = false;
        var mapCanvasDirty = _mapCanvasDirty;
        _mapCanvasDirty = false;
        if (!_mounted)
        {
            _canvasRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("grcsMapEditorMount", HostId, _canvasRef, CanvasPayload(), MapCanvasPayload());
            _mounted = true;
        }
        else if (mapCanvasDirty)
            await Js.InvokeVoidAsync("grcsMapEditorUpdateMapHost", HostId, MapCanvasPayload());
        if (_mounted && canvasDirty)
            await Js.InvokeVoidAsync("grcsMapEditorUpdate", HostId, CanvasPayload());
        if (_fitPending && _editor is not null)
        {
            await FitMap();
            _fitPending = false;
        }
        if (_focusPickMenu && _pickMenuOpen)
        {
            _focusPickMenu = false;
            await Js.InvokeVoidAsync("grcsMapEditorPlacePopup", HostId, _pickMenu);
            await _pickMenu.FocusAsync(preventScroll: true);
        }
    }

    private string MapCanvasPayload() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Stations = _editor?.Points.Select(p => new { Mark = p.PointCode, p.PointType, p.X, p.Y, StaEnable = p.IsEnabled }).ToArray() ?? [],
        Lines = _editor?.Lines.Select(l => new { l.LineCode, l.FromPointCode, l.ToPointCode, l.Direction, l.IsEnabled }).ToArray() ?? [],
        ShowLineDirections = true,
        InventoryByStation = _inventoryInstances
            .Where(x => x.MapCode == _editor?.MapCode && !string.IsNullOrWhiteSpace(x.PointCode))
            .GroupBy(x => x.PointCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                var pallets = items.Where(x => x.ItemType == RcsInventoryItemTypes.Pallet).ToArray();
                var nestedCargo = items.Where(x => x.ItemType == RcsInventoryItemTypes.Cargo
                    && !string.IsNullOrWhiteSpace(x.ParentInstanceCode)
                    && pallets.Any(pallet => string.Equals(pallet.InstanceCode, x.ParentInstanceCode, StringComparison.OrdinalIgnoreCase))).ToArray();
                var loadedPalletCodes = nestedCargo.Select(x => x.ParentInstanceCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var loadedPalletCount = pallets.Count(x => loadedPalletCodes.Contains(x.InstanceCode));
                return new
                {
                    Mark = group.Key,
                    PalletCount = pallets.Length,
                    PurePalletCount = pallets.Length - loadedPalletCount,
                    StandaloneCargoCount = items.Count(x => x.ItemType == RcsInventoryItemTypes.Cargo && string.IsNullOrWhiteSpace(x.ParentInstanceCode)),
                    NestedCargoCount = nestedCargo.Length,
                    LoadedPalletCount = loadedPalletCount
                };
            }).ToArray()
    });

    private string CanvasPayload() => System.Text.Json.JsonSerializer.Serialize(new
    {
        SelectedMark = _selectedPoint?.PointCode,
        SelectedLineCode = _selectedLine?.LineCode,
        StartMark = _showMoveDialog ? _moveStart : _startCode, EndMark = _showMoveDialog ? _moveEnd : _endCode,
        RoutePointCodes = VisibleRoutePointCodes,
        LockPointMark = SelectedVehicle?.LockPointCode,
        LockedPointCodes = SelectedVehicle?.LockedPointCodes ?? [],
        LockedLineCodes = SelectedVehicle?.LockedLineCodes ?? [],
        SelectedVehicleStatus = SelectedVehicle?.Status,
        Vehicles = _vehicles.Values.ToArray(),
        SelectedVehicleId = _selectedVehicleId,
        StationVisualScale = 0.56,
        EnableVehicleSelection = true,
        PickStationForMove = _showMoveDialog,
        EnableContextMenu = false
    });

    private async Task ReloadAsync(bool forceReload = false)
    {
        if (_showMoveDialog || _vehicleBusy) return;
        ClosePickMenu();
        _loading = true;
        try
        {
            _editor = await MapCache.GetActiveMapAsync(forceReload);
            await RefreshInventoryAsync();
            _selectedPoint = null; _selectedLine = null; _selectedVehicleId = ""; _dirty = false;
            _startCode = null; _endCode = null; _route = new();
            _feedback = ""; _feedbackError = false; _fitPending = true;
            _mapCanvasDirty = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _editor = null; _selectedPoint = null; _selectedLine = null; _selectedVehicleId = "";
            _startCode = null; _endCode = null; _route = new(); _dirty = false;
            _feedback = "没有已发布的当前地图，请先在编辑地图中保存。"; _feedbackError = false; _mapCanvasDirty = true;
        }
        catch (Exception ex) { _feedback = $"读取地图失败：{ex.Message}"; _feedbackError = true; }
        finally { _loading = false; _canvasDirty = true; }
    }

    private void OnPropertiesChanged() { _dirty = true; _mapCanvasDirty = true; ClearRoute(); }
    private async Task OnInventoryChanged()
    {
        await RefreshInventoryAsync();
        _mapCanvasDirty = true;
        _canvasDirty = true;
        StateHasChanged();
    }
    private void OnInventoryChangedRealtime() => _ = InvokeAsync(async () =>
    {
        if (_disposed) return;
        await OnInventoryChanged();
    });
    private async Task RefreshInventoryAsync()
    {
        _inventoryError = "";
        if (_editor is null) { _inventoryInstances = []; return; }
        try { _inventoryInstances = await Api.GetInventoryInstancesAsync(mapCode: _editor.MapCode); }
        catch (Exception ex)
        {
            _inventoryInstances = [];
            _inventoryError = ex.Message;
        }
    }
    private void ClearRoute() { _route = new(); _canvasDirty = true; }
    private void SetStart() { if (_selectedPoint is not null) _startCode = _selectedPoint.PointCode; ClearRoute(); }
    private void SetEnd() { if (_selectedPoint is not null) _endCode = _selectedPoint.PointCode; ClearRoute(); }
    private async Task SavePropertiesAsync()
    {
        if (_editor is null || !_dirty || _saving) return;
        _saving = true;
        try
        {
            await Api.SaveEditorMapAsync(_editor);
            MapCache.SetSavedMap(_editor);
            _dirty = false; ClearRoute(); _feedbackError = false;
            _feedback = "属性已保存到 RCS 数据库，地图缓存已刷新。";
        }
        catch (Exception ex) { _feedback = $"保存失败：{ex.Message}"; _feedbackError = true; }
        finally { _saving = false; _canvasDirty = true; }
    }

    [JSInvokable] public Task OnMapEditorStationClicked(string code)
    {
        if (_saving || _vehicleBusy) return Task.CompletedTask;
        if (_showMoveDialog) return _moveTaskDialog?.PickStationAsync(code) ?? Task.CompletedTask;
        ClosePickMenu(); _selectedVehicleId = "";
        _selectedPoint = _editor?.Points.FirstOrDefault(p => p.PointCode == code); _selectedLine = null;
        _canvasDirty = true; StateHasChanged(); return Task.CompletedTask;
    }
    [JSInvokable] public Task OnMapEditorLineClicked(string code)
    {
        if (_saving || _vehicleBusy || _showMoveDialog) return Task.CompletedTask;
        ClosePickMenu(); _selectedVehicleId = "";
        _selectedLine = _editor?.Lines.FirstOrDefault(l => l.LineCode == code); _selectedPoint = null;
        _canvasDirty = true; StateHasChanged(); return Task.CompletedTask;
    }
    [JSInvokable] public Task OnMapEditorCanvasPoint(double x, double y)
    {
        if (_saving || _vehicleBusy || _showMoveDialog) return Task.CompletedTask;
        ClosePickMenu(); _selectedVehicleId = "";
        _selectedPoint = null; _selectedLine = null; _canvasDirty = true; StateHasChanged(); return Task.CompletedTask;
    }
    [JSInvokable] public Task OnMapEditorBoxSelected(string[] codes)
    {
        return codes.Length == 1 ? OnMapEditorStationClicked(codes[0]) : OnMapEditorCanvasPoint(0, 0);
    }
    private async Task Preview()
    {
        if (!CanCalculate) return;
        try { _route = await Api.PreviewAsync(new RunVehicleRequest { StartPointCode = _startCode!, EndPointCode = _endCode! }) ?? new(); _feedback = _route.Message ?? ""; _feedbackError = !_route.Found; }
        catch (Exception ex) { _feedback = ex.Message; _feedbackError = true; }
        _canvasDirty = true;
    }
    private void OnVehicleStateChanged(VehicleStateDto state) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        _vehicles[state.Id] = state; SelectActiveVehicleIfNone(); _canvasDirty = true; StateHasChanged();
    });
    private void OnVehiclesChanged(IReadOnlyList<VehicleStateDto> states) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        _vehicles.Clear(); foreach (var state in states) _vehicles[state.Id] = state;
        ClearMissingVehicleSelection();
        SelectActiveVehicleIfNone();
        if (!PickVehicles.Any() && !PickPoints.Any()) ClosePickMenu();
        _canvasDirty = true; StateHasChanged();
    });
    private void OnTaskChanged(RcsTaskDto task) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        _tasks[task.TaskId] = task;
        SelectActiveVehicleIfNone();
        _canvasDirty = true;
        StateHasChanged();
    });
    private void OnTasksCleared(int deletedCount) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        _tasks.Clear();
        _canvasDirty = true;
        StateHasChanged();
    });
    private async Task RefreshVehiclesAsync()
    {
        try
        {
            var states = await Api.GetVehiclesAsync();
            if (_disposed) return;
            _vehicles.Clear(); foreach (var state in states) _vehicles[state.Id] = state;
            ClearMissingVehicleSelection();
            if (!PickVehicles.Any() && !PickPoints.Any()) ClosePickMenu();
            _canvasDirty = true;
        }
        catch (Exception ex) { if (!_disposed) { _feedback = $"读取车辆失败：{ex.Message}"; _feedbackError = true; } }
    }
    private async Task RefreshTasksAsync()
    {
        try
        {
            var tasks = await Api.GetTasksAsync(500);
            if (_disposed) return;
            _tasks.Clear(); foreach (var task in tasks) _tasks[task.TaskId] = task;
            SelectActiveVehicleIfNone();
            _canvasDirty = true;
        }
        catch (Exception ex) { if (!_disposed) { _feedback = $"读取任务路径失败：{ex.Message}"; _feedbackError = true; } }
    }
    private void OnReconnected() => _ = InvokeAsync(async () =>
    {
        if (_disposed) return;
        await RefreshVehiclesAsync(); await RefreshTasksAsync(); StateHasChanged();
    });
    private void SelectActiveVehicleIfNone()
    {
        if (!string.IsNullOrEmpty(_selectedVehicleId) || _selectedPoint is not null || _selectedLine is not null) return;
        _selectedVehicleId = _vehicles.Values
            .Where(vehicle => vehicle.Status is "Running" or "Paused")
            .OrderBy(vehicle => vehicle.Id, StringComparer.Ordinal)
            .FirstOrDefault(vehicle => _tasks.TryGetValue(vehicle.TaskId, out var task)
                && task.Status is RcsTaskStatus.Running or RcsTaskStatus.Paused or RcsTaskStatus.Cancelling)?.Id ?? "";
    }
    [JSInvokable] public Task OnRuntimeMapVehicleClicked(string id) => SelectVehicle(id);
    private Task SelectVehicle(string id)
    {
        if (_saving || _vehicleBusy || _showMoveDialog || !_vehicles.ContainsKey(id)) return Task.CompletedTask;
        ClosePickMenu(); _selectedVehicleId = id; _selectedPoint = null; _selectedLine = null;
        _canvasDirty = true; StateHasChanged(); return Task.CompletedTask;
    }
    [JSInvokable] public Task OnRuntimeMapOverlap(string[] vehicleIds, string[] pointCodes, double x, double y)
    {
        if (_saving || _vehicleBusy || _showMoveDialog || !double.IsFinite(x) || !double.IsFinite(y)) return Task.CompletedTask;
        _pickVehicleIds = vehicleIds.Where(_vehicles.ContainsKey).Distinct().ToArray();
        _pickPointCodes = pointCodes.Where(code => _editor?.Points.Any(p => p.PointCode == code) == true).Distinct().ToArray();
        _pickMenuX = x; _pickMenuY = y; _pickMenuOpen = _pickVehicleIds.Length + _pickPointCodes.Length > 0;
        _focusPickMenu = _pickMenuOpen; StateHasChanged(); return Task.CompletedTask;
    }
    private void ClosePickMenu() { _pickMenuOpen = false; _focusPickMenu = false; }
    private void OnPickMenuKeyDown(KeyboardEventArgs args) { if (args.Key == "Escape") ClosePickMenu(); }
    private void OnVehicleBusyChanged(bool busy) => _vehicleBusy = busy;
    private void OnVehicleOperationFailed(string message) { _feedback = message; _feedbackError = true; }
    private void OpenMoveDialog()
    {
        if (_saving || _vehicleBusy || _dirty || _showMoveDialog || SelectedVehicle is not { IsEnabled: true, OperatingMode: RcsOperatingMode.Manual } car || car.Status is "Running" or "Paused") return;
        ClosePickMenu(); _showMoveDialog = true; _moveStart = car.PointCode; _moveEnd = "";
        _route = new(); _canvasDirty = true;
    }
    private async Task ToggleVehicleModeAsync()
    {
        if (_saving || _vehicleBusy || _showMoveDialog || SelectedVehicle is not { } car || car.Status is "Running" or "Paused") return;
        _saving = true;
        try
        {
            var mode = car.OperatingMode == RcsOperatingMode.Manual ? RcsOperatingMode.Automatic : RcsOperatingMode.Manual;
            var updated = await Api.UpdateVehicleAsync(car.Id, new UpdateVehicleRequest
                { Name = car.Name, IsEnabled = car.IsEnabled, OperatingMode = mode });
            if (updated is not null) _vehicles[updated.Id] = updated;
            _feedback = $"车辆 {car.Id} 已切换为{(mode == RcsOperatingMode.Manual ? "手动" : "自动")}模式。";
            _feedbackError = false; _canvasDirty = true;
        }
        catch (Exception ex) { _feedback = $"切换车辆模式失败：{ex.Message}"; _feedbackError = true; }
        finally { _saving = false; }
    }
    private void CloseMoveDialog()
    {
        if (_vehicleBusy) return;
        _showMoveDialog = false; _moveStart = ""; _moveEnd = ""; _canvasDirty = true;
    }
    private void OnMovePointsChanged(RunVehicleRequest request)
    {
        _moveStart = request.StartPointCode; _moveEnd = request.EndPointCode; _canvasDirty = true;
    }
    private async Task OnMoveCompleted(string message)
    {
        CloseMoveDialog(); await OnVehicleOperationCompleted(message);
    }
    private void ClearMissingVehicleSelection()
    {
        if (_vehicles.ContainsKey(_selectedVehicleId)) return;
        _selectedVehicleId = ""; _showMoveDialog = false; _moveStart = ""; _moveEnd = ""; _vehicleBusy = false;
    }
    private async Task OnVehicleOperationCompleted(string message)
    {
        _feedback = message; _feedbackError = false; await RefreshVehiclesAsync();
    }
    private static string VehicleStatus(VehicleStateDto vehicle) => (vehicle.Status switch { "Running" => "运行中", "Paused" => "已暂停", "Arrived" => "已到达", "ProtocolUnavailable" => "协议未注册", _ => "空闲" }) + (vehicle.IsEnabled ? "" : " · 停用");
    private Task ZoomIn() => Js.InvokeVoidAsync("grcsMapEditorZoomHost", HostId, 1.2).AsTask();
    private Task ZoomOut() => Js.InvokeVoidAsync("grcsMapEditorZoomHost", HostId, 1 / 1.2).AsTask();
    private Task FitMap() => Js.InvokeVoidAsync("grcsMapEditorFitHost", HostId).AsTask();
    public async ValueTask DisposeAsync()
    {
        _disposed = true; Realtime.VehicleStateChanged -= OnVehicleStateChanged; Realtime.InventoryChanged -= OnInventoryChangedRealtime;
        Realtime.VehiclesChanged -= OnVehiclesChanged;
        Realtime.TaskChanged -= OnTaskChanged;
        Realtime.TasksCleared -= OnTasksCleared;
        Realtime.Reconnected -= OnReconnected;
        if (_mounted) { try { await Js.InvokeVoidAsync("grcsMapEditorDisposeHost", HostId); } catch (JSException) { } }
        _canvasRef?.Dispose();
    }
}
