using System.Text.Json;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Dashboard.Modules.WcsSimulator.Models;
using Dashboard.Modules.WcsSimulator.Services;
using Contracts.Dtos;

namespace Dashboard.Modules.WcsSimulator.Pages;

public partial class MapEditor
{
    internal bool IsSectionCollapsed(string key) => IsCollapsed(key);

    internal readonly List<MapStationLite> _createdMapStations = [];
    internal readonly HashSet<string> _selectedCreatedMarks = new(StringComparer.OrdinalIgnoreCase);
    internal MapStationLite? _selectedCreatedStation;
    internal MapStationLite? _alignmentAnchor;
    internal string _selectedCreatedCargoAreas = "";
    internal string _mapBuilderTool = "select";
    internal bool _pointToolsExpanded;
    internal int _nextCreatedStationIndex = 1;
    internal bool _matrixDialogOpen;
    internal int _matrixStationType = MapStationTypeBits.StorageLocation;
    internal double _matrixOriginX;
    internal double _matrixOriginY;
    internal int _matrixRows = 3;
    internal int _matrixColumns = 3;
    internal double _matrixHorizontalSpacing = 1000;
    internal double _matrixVerticalSpacing = 1000;
    internal string _matrixDirection = "down";
    internal DotNetObjectReference<MapEditor>? _mapEditorRef;
    internal bool _mapEditorDirty = true;
    internal bool _mapSaving;
    internal bool _mapLoading;
    internal bool _fitMapAfterUpdate;
    internal bool _mapSaveSucceeded;
    internal string _mapSaveMessage = "";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // 编辑地图和库存管理都从 WCS 库存站点表读取地图，不再使用浏览器地图缓存。
            var savedMap = await WcsApi.GetInventoryMapAsync();
            if (savedMap?.Stations is { Count: > 0 })
            {
                _createdMapStations.AddRange(savedMap.Stations);
                var highestIndex = savedMap.Stations
                    .Select(station => station.Mark)
                    .Where(mark => mark.StartsWith("S", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(mark.AsSpan(1), out _))
                    .Select(mark => int.TryParse(mark.AsSpan(1), out var index) ? index : 0)
                    .DefaultIfEmpty(0).Max();
                _nextCreatedStationIndex = Math.Max(1, highestIndex + 1);
            }
            _mapEditorDirty = false;
        }
        catch (Exception ex)
        {
            _mapSaveMessage = $"读取已保存地图失败：{ex.Message}";
            _mapSaveSucceeded = false;
        }
    }

    internal void SetMapBuilderTool(string tool)
    {
        _mapBuilderTool = tool;
        if (tool is not "align-x" and not "align-y") _alignmentAnchor = null;
    }
    internal void TogglePointTools() => _pointToolsExpanded = !_pointToolsExpanded;
    internal void MarkMapEditorDirty() => _mapEditorDirty = true;

    internal async Task LoadInventoryMap()
    {
        if (_mapLoading || _mapSaving) return;

        _mapLoading = true;
        _mapSaveMessage = "";
        try
        {
            var savedMap = await WcsApi.GetInventoryMapAsync();
            if (savedMap?.Stations == null)
            {
                _mapSaveMessage = "读取 WCS 后端地图失败，请检查后端连接";
                _mapSaveSucceeded = false;
                return;
            }

            var stations = savedMap.Stations.Where(station => station.StationType != 0).ToList();
            _createdMapStations.Clear();
            _createdMapStations.AddRange(stations);
            _nextCreatedStationIndex = NextSavedStationIndex(stations);
            _selectedCreatedMarks.Clear();
            _selectedCreatedStation = null;
            _selectedCreatedCargoAreas = "";
            _alignmentAnchor = null;
            _mapEditorDirty = true;
            _fitMapAfterUpdate = true;

            _mapSaveMessage = $"已从 WCS 后端读取地图（{stations.Count} 个站点）";
            _mapSaveSucceeded = true;
        }
        catch (Exception ex)
        {
            _mapSaveMessage = $"读取 WCS 后端地图失败：{ex.Message}";
            _mapSaveSucceeded = false;
        }
        finally
        {
            _mapLoading = false;
        }
    }

    internal async Task SaveCreatedMap()
    {
        if (_mapSaving || _mapLoading) return;

        _mapSaving = true;
        _mapSaveMessage = "";
        try
        {
            var savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var stations = _createdMapStations.Select(station => new MapStationLite
            {
                Mark = station.Mark.Trim(),
                StationType = station.StationType,
                X = station.X,
                Y = station.Y,
                Floor = station.Floor,
                StaEnable = station.StaEnable,
                AllowTurn = station.AllowTurn,
                AllowAvoid = station.AllowAvoid,
                AllowStop = station.AllowStop,
                SupportLoadState = station.SupportLoadState,
                CargoAreas = station.CargoAreas?.ToList() ?? [],
            }).ToList();

            if (stations.Any(station => string.IsNullOrWhiteSpace(station.Mark)))
            {
                _mapSaveMessage = "站点编码不能为空";
                _mapSaveSucceeded = false;
                return;
            }
            var duplicate = stations.GroupBy(station => station.Mark, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                _mapSaveMessage = $"站点编码重复：{duplicate.Key}";
                _mapSaveSucceeded = false;
                return;
            }

            var (ok, response) = await WcsApi.SaveInventoryMapAsync(new MapUploadPayload
            {
                SavedAt = savedAt,
                PathsCount = 0,
                Stations = stations,
            });
            if (!ok)
            {
                _mapSaveMessage = TryReadApiMessage(response) ?? $"地图入库失败：{response}";
                _mapSaveSucceeded = false;
                return;
            }

            _createdMapStations.Clear();
            _createdMapStations.AddRange(stations);
            _nextCreatedStationIndex = Math.Max(_nextCreatedStationIndex, NextSavedStationIndex(stations));
            _mapEditorDirty = true;
            _mapSaveMessage = stations.Count == 0
                ? "已保存空地图；原库存地图已清空"
                : $"已入库 {stations.Count} 个站点；原库存地图已替换";
            _mapSaveSucceeded = true;
            ShowFeedback("✅", _mapSaveMessage);
        }
        catch (Exception ex)
        {
            _mapSaveMessage = $"地图入库失败：{ex.Message}";
            _mapSaveSucceeded = false;
        }
        finally
        {
            _mapSaving = false;
        }
    }

    private static string? TryReadApiMessage(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("message", out var message))
                return message.GetString();
        }
        catch { }
        return null;
    }

    private static int NextSavedStationIndex(IEnumerable<MapStationLite> stations)
        => stations.Select(station => station.Mark)
            .Where(mark => mark.StartsWith("S", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(mark.AsSpan(1), out _))
            .Select(mark => int.TryParse(mark.AsSpan(1), out var index) ? index : 0)
            .DefaultIfEmpty(0).Max() + 1;

    internal string NextCreatedStationMark()
    {
        while (_createdMapStations.Any(s => s.Mark.Equals($"S{_nextCreatedStationIndex:000}", StringComparison.OrdinalIgnoreCase)))
            _nextCreatedStationIndex++;
        return $"S{_nextCreatedStationIndex++:000}";
    }
    internal object BuildMapEditorPayload() => new
    {
        Stations = _createdMapStations.Select(s => new { s.Mark, s.StationType, s.X, s.Y, s.StaEnable }),
        ScaleMapElementsWithZoom = true,
        StationVisualScale = 0.56,
        SelectedMark = _selectedCreatedStation?.Mark,
        SelectedMarks = _selectedCreatedMarks,
        AnchorMark = _alignmentAnchor?.Mark,
    };
    internal void OpenMatrixDialog()
    {
        _mapBuilderTool = "matrix";
        _matrixDialogOpen = true;
    }
    internal void CloseMatrixDialog()
    {
        _matrixDialogOpen = false;
        if (_mapBuilderTool == "matrix") _mapBuilderTool = "select";
    }
    internal void CreateMatrixStations()
    {
        var rows = Math.Clamp(_matrixRows, 1, 200);
        var columns = Math.Clamp(_matrixColumns, 1, 200);
        var horizontal = Math.Max(0, _matrixHorizontalSpacing);
        var vertical = Math.Max(0, _matrixVerticalSpacing);
        var (rowX, rowY, colX, colY) = _matrixDirection switch
        {
            "up" => (0d, vertical, horizontal, 0d),
            "left" => (-horizontal, 0d, 0d, vertical),
            "right" => (horizontal, 0d, 0d, vertical),
            _ => (0d, -vertical, horizontal, 0d),
        };
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var mark = NextCreatedStationMark();
            _createdMapStations.Add(new MapStationLite
            {
                Mark = mark,
                StationType = _matrixStationType,
                X = _matrixOriginX + row * rowX + column * colX,
                Y = _matrixOriginY + row * rowY + column * colY,
                Floor = 1,
                StaEnable = true,
                AllowTurn = true,
                AllowAvoid = true,
                AllowStop = true,
                CargoAreas = [],
            });
        }
        CloseMatrixDialog();
        _mapEditorDirty = true;
    }
    internal Task ZoomMapIn() => Js.InvokeVoidAsync("grcsMapEditorZoomHost", "map-create-canvas-host", 1.2).AsTask();
    internal Task ZoomMapOut() => Js.InvokeVoidAsync("grcsMapEditorZoomHost", "map-create-canvas-host", 1 / 1.2).AsTask();
    internal Task ResetMapZoom() => Js.InvokeVoidAsync("grcsMapEditorFitHost", "map-create-canvas-host").AsTask();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _mapEditorRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("grcsMapEditorMount", "map-create-canvas-host", _mapEditorRef,
                JsonSerializer.Serialize(BuildMapEditorPayload()));
            _mapEditorDirty = false;
            await Js.InvokeVoidAsync("grcsMapEditorFitHost", "map-create-canvas-host");
        }
        else if (_mapEditorDirty)
        {
            await Js.InvokeVoidAsync("grcsMapEditorUpdate", "map-create-canvas-host",
                JsonSerializer.Serialize(BuildMapEditorPayload()));
            _mapEditorDirty = false;
            if (_fitMapAfterUpdate)
            {
                _fitMapAfterUpdate = false;
                await Js.InvokeVoidAsync("grcsMapEditorFitHost", "map-create-canvas-host");
            }
        }
    }

    [JSInvokable]
    public Task OnMapEditorCanvasPoint(double x, double y)
    {
        if (!IsCreationTool(_mapBuilderTool))
        {
            _selectedCreatedStation = null;
            _selectedCreatedMarks.Clear();
            _mapEditorDirty = true;
            StateHasChanged();
            return Task.CompletedTask;
        }
        var mark = NextCreatedStationMark();
        var station = new MapStationLite
        {
            Mark = mark,
            StationType = CreationToolType(_mapBuilderTool),
            X = Math.Clamp(x, -100000, 100000),
            // SVG 屏幕坐标 Y 向下，转换为笛卡尔坐标后 Y 向上，原点位于画布左下方。
            Y = Math.Clamp(y, -100000, 100000),
            Floor = 1,
            StaEnable = true,
            AllowTurn = true,
            AllowAvoid = true,
            AllowStop = true,
            CargoAreas = [],
        };
        _createdMapStations.Add(station);
        _selectedCreatedMarks.Clear();
        _selectedCreatedMarks.Add(station.Mark);
        _selectedCreatedStation = station;
        _selectedCreatedCargoAreas = "";
        _mapBuilderTool = "select";
        _mapEditorDirty = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorStationClicked(string mark)
    {
        var station = _createdMapStations.FirstOrDefault(s => s.Mark.Equals(mark, StringComparison.OrdinalIgnoreCase));
        if (station != null)
        {
            SelectCreatedStation(station);
            if (_mapBuilderTool is not "align-x" and not "align-y")
            {
                _selectedCreatedMarks.Clear();
                _selectedCreatedMarks.Add(station.Mark);
            }
        }
        _mapEditorDirty = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorBoxSelected(string[] marks)
    {
        _selectedCreatedMarks.Clear();
        foreach (var mark in marks ?? [])
            if (_createdMapStations.Any(s => s.Mark.Equals(mark, StringComparison.OrdinalIgnoreCase))) _selectedCreatedMarks.Add(mark);
        _selectedCreatedStation = _selectedCreatedMarks.Count == 1
            ? _createdMapStations.FirstOrDefault(s => s.Mark.Equals(_selectedCreatedMarks.First(), StringComparison.OrdinalIgnoreCase))
            : null;
        _selectedCreatedCargoAreas = _selectedCreatedStation == null ? "" : string.Join(", ", _selectedCreatedStation.CargoAreas);
        _mapEditorDirty = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    internal static bool IsCreationTool(string tool) => tool is "storage" or "transfer" or "people" or "sorting";
    internal static int CreationToolType(string tool) => tool switch
    {
        "transfer" => MapStationTypeBits.TransferPoint,
        "people" => MapStationTypeBits.PeopleStation,
        "sorting" => MapStationTypeBits.PickingStation,
        _ => MapStationTypeBits.StorageLocation,
    };
    internal List<MapStationLite> SelectedCreatedStations => _createdMapStations.Where(s => _selectedCreatedMarks.Contains(s.Mark)).ToList();
    internal static string MixedText<T>(IEnumerable<MapStationLite> stations, Func<MapStationLite, T> selector)
    {
        var values = stations.Select(selector).Distinct().ToList();
        return values.Count == 1 ? values[0]?.ToString() ?? "" : "";
    }
    internal string BatchXText => MixedText(SelectedCreatedStations, s => s.X);
    internal string BatchYText => MixedText(SelectedCreatedStations, s => s.Y);
    internal string BatchFloorText => MixedText(SelectedCreatedStations, s => s.Floor);
    internal string BatchCargoAreasText => MixedText(SelectedCreatedStations, s => string.Join(", ", s.CargoAreas));
    internal string BatchEnabledText => MixedText(SelectedCreatedStations, s => s.StaEnable ? "true" : "false");
    internal void ApplyBatch(Action<MapStationLite> apply) { foreach (var station in SelectedCreatedStations) apply(station); _mapEditorDirty = true; }
    internal void OnBatchXInput(ChangeEventArgs e) { if (double.TryParse(e.Value?.ToString(), out var v)) ApplyBatch(s => s.X = v); }
    internal void OnBatchYInput(ChangeEventArgs e) { if (double.TryParse(e.Value?.ToString(), out var v)) ApplyBatch(s => s.Y = v); }
    internal void OnBatchFloorInput(ChangeEventArgs e) { if (int.TryParse(e.Value?.ToString(), out var v)) ApplyBatch(s => s.Floor = v); }
    internal void OnBatchCargoAreasInput(ChangeEventArgs e)
    {
        var areas = (e.Value?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ApplyBatch(s => s.CargoAreas = [.. areas]);
    }
    internal void OnBatchEnabledChanged(ChangeEventArgs e) { if (bool.TryParse(e.Value?.ToString(), out var v)) ApplyBatch(s => s.StaEnable = v); }

    public void Dispose() => _mapEditorRef?.Dispose();

    internal void SelectCreatedStation(MapStationLite station)
    {
        if (_mapBuilderTool is "align-x" or "align-y")
        {
            if (_alignmentAnchor == null)
            {
                _alignmentAnchor = station;
            }
            else if (!ReferenceEquals(_alignmentAnchor, station))
            {
                if (_mapBuilderTool == "align-x") station.X = _alignmentAnchor.X;
                else station.Y = _alignmentAnchor.Y;
            }
        }
        _selectedCreatedStation = station;
        _selectedCreatedMarks.Clear();
        _selectedCreatedMarks.Add(station.Mark);
        _selectedCreatedCargoAreas = string.Join(", ", station.CargoAreas);
        _mapEditorDirty = true;
    }

    internal void OnSelectedCreatedCargoAreasInput(ChangeEventArgs e)
    {
        _selectedCreatedCargoAreas = e.Value?.ToString() ?? "";
        if (_selectedCreatedStation != null)
        {
            _selectedCreatedStation.CargoAreas = _selectedCreatedCargoAreas
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    internal void DeleteSelectedCreatedStation()
    {
        if (_selectedCreatedStation == null) return;
        _createdMapStations.Remove(_selectedCreatedStation);
        _selectedCreatedStation = null;
        _selectedCreatedMarks.Clear();
        _alignmentAnchor = null;
        _selectedCreatedCargoAreas = "";
        _mapEditorDirty = true;
    }

    internal void DeleteSelectedCreatedStations()
    {
        if (_selectedCreatedMarks.Count == 0) return;
        _createdMapStations.RemoveAll(s => _selectedCreatedMarks.Contains(s.Mark));
        _selectedCreatedMarks.Clear();
        _selectedCreatedStation = null;
        _selectedCreatedCargoAreas = "";
        _alignmentAnchor = null;
        _mapEditorDirty = true;
    }

    internal void ClearCreatedMap()
    {
        _createdMapStations.Clear();
        _nextCreatedStationIndex = 1;
        _selectedCreatedMarks.Clear();
        _selectedCreatedStation = null;
        _selectedCreatedCargoAreas = "";
        _alignmentAnchor = null;
        _mapEditorDirty = true;
    }

    internal sealed class CanvasPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
