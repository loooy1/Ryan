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

public partial class RcsMapEditor
{
    private RcsMapEditorDto _map = new() { MapCode = "default", Name = "RCS 主地图", SceneName = "AMR", Status = "PUBLISHED", IsActive = true };
    private EditorTool _tool = EditorTool.Select;
    private RcsMapPointDto? _selectedPoint;
    private RcsMapLineDto? _selectedLine;
    private RcsMapPointDto? _pendingLinePoint;
    private RcsMapPointDto? _alignmentAnchor;
    private bool _matrixDialogOpen;
    private bool _pointToolsExpanded;
    private bool _pathToolsExpanded;
    private readonly HashSet<string> _rangeSelection = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPointCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedLineCodes = new(StringComparer.OrdinalIgnoreCase);
    private bool _contextMenuOpen;
    private bool _stationFilterMenuOpen;
    private double _contextMenuX;
    private double _contextMenuY;
    private bool _saving;
    private bool _error;
    private string _message = "";
    private string _matrixType = "STORAGE";
    private double _matrixOriginX;
    private double _matrixOriginY;
    private int _matrixRows = 3;
    private int _matrixColumns = 3;
    private double _matrixHorizontalSpacing = 80;
    private double _matrixVerticalSpacing = 80;
    private DotNetObjectReference<RcsMapEditor>? _mapEditorRef;
    private bool _mapEditorDirty = true;
    private bool _fitPending;
    private bool _importing;
    private int _mapFileInputKey;

    private string ToolHint => _tool switch
    {
        EditorTool.Storage => "点击画布放置储位",
        EditorTool.Transfer => "点击画布放置接驳位",
        EditorTool.People => "点击画布放置人工分拣台",
        EditorTool.Sorting => "点击画布放置分拣台",
        EditorTool.Waypoint => "点击画布放置路径点",
        EditorTool.Elevator => "点击画布放置跨层点",
        EditorTool.Charging => "点击画布放置充电点",
        EditorTool.Line => _pendingLinePoint is null ? "请选择起点" : "请选择终点",
        EditorTool.RangeLine => _rangeSelection.Count == 0 ? "拖拽框选站点" : $"已选择 {_rangeSelection.Count} 个站点，请确认连接",
        EditorTool.Matrix => "配置点阵后批量生成",
        EditorTool.AlignX => "先选基准点，再选需要对齐的点",
        EditorTool.AlignY => "先选基准点，再选需要对齐的点",
        _ => "中键/右键拖动画布 · 滚轮缩放 · X向右 · Y向上"
    };

    protected override async Task OnInitializedAsync()
    {
        try { var saved = await Js.InvokeAsync<string?>("localStorage.getItem", "rcs_backend_url"); if (!string.IsNullOrWhiteSpace(saved)) Api.SetBaseUrl(saved); } catch { }
        await ReloadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _mapEditorRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("grcsMapEditorMount", "rcs-map-editor-host", _mapEditorRef,
                System.Text.Json.JsonSerializer.Serialize(BuildMapEditorPayload()));
            _mapEditorDirty = false;
        }
        else if (_mapEditorDirty)
        {
            await Js.InvokeVoidAsync("grcsMapEditorUpdate", "rcs-map-editor-host",
                System.Text.Json.JsonSerializer.Serialize(BuildMapEditorPayload()));
            _mapEditorDirty = false;
        }

        if (_fitPending && _map.Points.Count > 0)
        {
            await Js.InvokeVoidAsync("grcsMapEditorFitHost", "rcs-map-editor-host");
            _fitPending = false;
        }
    }

    private async Task ReloadAsync(bool forceReload = false)
    {
        try { var loaded = await MapCache.GetEditorMapAsync(_map.MapCode, forceReload); if (loaded is not null) _map = loaded; _message = loaded is null ? "尚未保存地图，可以开始创建。" : "地图已读取。"; _error = false; _mapEditorDirty = true; _fitPending = loaded is { Points.Count: > 0 }; }
        catch (Exception ex) { _message = $"读取失败：{ex.Message}"; _error = true; }
    }

    private async Task ImportGrcsMapAsync(InputFileChangeEventArgs args)
    {
        var file = args.File;
        _importing = true;
        _message = "";
        _error = false;
        try
        {
            await using var stream = file.OpenReadStream(16 * 1024 * 1024);
            using var document = await JsonDocument.ParseAsync(stream);
            var imported = ParseGrcsMap(document.RootElement);
            imported.MapCode = string.IsNullOrWhiteSpace(_map.MapCode) ? "default" : _map.MapCode.Trim();
            imported.Name = string.IsNullOrWhiteSpace(imported.Name) ? "GRCS 地图" : imported.Name;
            imported.SceneName = string.IsNullOrWhiteSpace(imported.SceneName) ? _map.SceneName : imported.SceneName;
            imported.Version = Math.Max(1, _map.Version);
            imported.Status = "PUBLISHED";
            imported.IsActive = true;
            imported.SourceType = "GRCS";
            imported.CoordinateSystem = "WORLD";
            imported.Description = $"从 GRCS 地图文件 {file.Name} 导入；原始站点和路径字段保存在元数据中。";

            // RCS 保存接口会在同一地图编码下替换点/线路，并刷新算法地图缓存。
            await Api.SaveEditorMapAsync(imported);
            MapCache.SetSavedMap(imported);
            _map = imported;
            _selectedPoint = null;
            _selectedLine = null;
            _selectedPointCodes.Clear();
            _selectedLineCodes.Clear();
            _rangeSelection.Clear();
            _pendingLinePoint = null;
            _tool = EditorTool.Select;
            CloseContextMenu();
            _mapEditorDirty = true;
            _fitPending = imported.Points.Count > 0;
            _message = $"GRCS 地图导入成功并已写入 RCS：{_map.Points.Count} 个站点、{_map.Lines.Count} 条路径。";
        }
        catch (Exception ex)
        {
            _message = $"GRCS 地图导入失败：{ex.Message}";
            _error = true;
        }
        finally
        {
            _importing = false;
            _mapFileInputKey++;
        }
    }

    private static RcsMapEditorDto ParseGrcsMap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("地图 JSON 根节点必须是对象。");

        var stationsElement = FindProperty(root, "stations");
        var pathsElement = FindProperty(root, "paths");
        if (stationsElement.ValueKind != JsonValueKind.Object || pathsElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("未找到 GRCS 地图所需的 stations 和 paths 对象。");

        var map = new RcsMapEditorDto
        {
            Name = ReadString(FindProperty(root, "name")),
            SceneName = ReadString(FindProperty(root, "scene")),
            SourceType = "GRCS",
            Status = "PUBLISHED",
            CoordinateSystem = "WORLD",
            IsActive = true,
            Version = 1,
        };

        var rootFloor = ReadDouble(FindProperty(root, "floor"), 1);
        var pointCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pointByCode = new Dictionary<string, RcsMapPointDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var stationProperty in stationsElement.EnumerateObject())
        {
            var station = stationProperty.Value;
            var code = ReadString(FindProperty(station, "mark"), stationProperty.Name).Trim();
            if (string.IsNullOrWhiteSpace(code) || !pointCodes.Add(code))
                throw new InvalidDataException($"站点编码为空或重复：{code}。");

            var point = new RcsMapPointDto
            {
                PointCode = code,
                PointType = MapGrcsStationType(ReadInt(FindProperty(station, "stationType"), 0)),
                X = ReadDouble(FindProperty(station, "x"), 0),
                Y = ReadDouble(FindProperty(station, "y"), 0),
                Z = ReadDouble(FindProperty(station, "floor"), rootFloor),
                IsEnabled = ReadBool(FindProperty(station, "staEnable"), true),
                MetadataJson = station.GetRawText(),
            };
            map.Points.Add(point);
            pointByCode.Add(code, point);
        }

        var lineCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pathProperty in pathsElement.EnumerateObject())
        {
            var path = pathProperty.Value;
            var from = ReadString(FindProperty(path, "startName")).Trim();
            var to = ReadString(FindProperty(path, "endName")).Trim();
            if (!pointByCode.ContainsKey(from) || !pointByCode.ContainsKey(to))
                throw new InvalidDataException($"路径 {pathProperty.Name} 的端点 {from} → {to} 不在站点列表中。");

            var lineCode = ReadString(FindProperty(path, "id"), pathProperty.Name).Trim();
            if (string.IsNullOrWhiteSpace(lineCode) || !lineCodes.Add(lineCode))
                throw new InvalidDataException($"路径编码为空或重复：{lineCode}。");

            var start = FindProperty(path, "startPos");
            var end = FindProperty(path, "endPos");
            var startX = ReadDouble(FindProperty(start, "x"), pointByCode[from].X);
            var startY = ReadDouble(FindProperty(start, "y"), pointByCode[from].Y);
            var endX = ReadDouble(FindProperty(end, "x"), pointByCode[to].X);
            var endY = ReadDouble(FindProperty(end, "y"), pointByCode[to].Y);
            var driveDirection = ReadInt(FindProperty(path, "driveDirection"), 0);

            map.Lines.Add(new RcsMapLineDto
            {
                LineCode = lineCode,
                FromPointCode = from,
                ToPointCode = to,
                Distance = Math.Sqrt(Math.Pow(endX - startX, 2) + Math.Pow(endY - startY, 2)),
                Direction = driveDirection == 0 ? "BIDIRECTIONAL" : "FORWARD",
                MaxSpeed = ReadDouble(FindProperty(path, "agvSpeed"), 0),
                // GRCS 的 routeEnable 在该地图中并未标记可通行状态，保留原值在元数据；
                // RCS 的 is_enabled 控制算法能否使用路径，因此导入的实际路径设为启用。
                IsEnabled = true,
                MetadataJson = path.GetRawText(),
            });
        }

        if (map.Points.Count == 0 || map.Lines.Count == 0)
            throw new InvalidDataException("地图必须至少包含一个站点和一条路径。");
        return map;
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string ReadString(JsonElement element, string fallback = "") =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : fallback;

    private static int ReadInt(JsonElement element, int fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : fallback;

    private static double ReadDouble(JsonElement element, double fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : fallback;

    private static bool ReadBool(JsonElement element, bool fallback) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : fallback;

    private static string MapGrcsStationType(int stationType)
    {
        if ((stationType & 4) != 0) return "STORAGE";
        if ((stationType & 8) != 0) return "TRANSFER";
        if ((stationType & 128) != 0) return "PEOPLE";
        if ((stationType & 64) != 0) return "SORTING";
        if ((stationType & 32) != 0) return "CHARGING";
        if ((stationType & 256) != 0) return "ELEVATOR";
        return "WAYPOINT";
    }

    private void SetTool(EditorTool tool) { _tool = tool; _selectedPoint = null; _selectedLine = null; _pendingLinePoint = null; _selectedPointCodes.Clear(); _selectedLineCodes.Clear(); _rangeSelection.Clear(); CloseContextMenu(); if (tool is not EditorTool.AlignX and not EditorTool.AlignY) _alignmentAnchor = null; if (tool is EditorTool.Storage or EditorTool.Transfer or EditorTool.People or EditorTool.Sorting or EditorTool.Waypoint or EditorTool.Elevator or EditorTool.Charging) _pointToolsExpanded = true; _mapEditorDirty = true; }
    private void TogglePointTools() => _pointToolsExpanded = !_pointToolsExpanded;
    private void TogglePathTools() => _pathToolsExpanded = !_pathToolsExpanded;
    private string ToolClass(EditorTool tool) => _tool == tool ? "active" : "";
    private void MarkDirty() { _mapEditorDirty = true; StateHasChanged(); }
    private void RenameSelectedPoint(ChangeEventArgs e)
    {
        if (_selectedPoint is null) return;
        var oldCode = _selectedPoint.PointCode;
        var newCode = e.Value?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newCode) || _map.Points.Any(x => !ReferenceEquals(x, _selectedPoint) && x.PointCode.Equals(newCode, StringComparison.OrdinalIgnoreCase))) return;
        _selectedPoint.PointCode = newCode;
        foreach (var line in _map.Lines)
        {
            if (line.FromPointCode.Equals(oldCode, StringComparison.OrdinalIgnoreCase)) line.FromPointCode = newCode;
            if (line.ToPointCode.Equals(oldCode, StringComparison.OrdinalIgnoreCase)) line.ToPointCode = newCode;
        }
        MarkDirty();
    }

    private void SelectPoint(RcsMapPointDto point)
    {
        if (_tool == EditorTool.Line)
        {
            if (_pendingLinePoint is null) { _pendingLinePoint = point; _selectedPoint = point; }
            else if (_pendingLinePoint != point)
            {
                _map.Lines.Add(new RcsMapLineDto { LineCode = NextLineCode(), FromPointCode = _pendingLinePoint.PointCode, ToPointCode = point.PointCode, Distance = Distance(_pendingLinePoint, point), Direction = "BIDIRECTIONAL", IsEnabled = true });
                _selectedLine = null; _selectedPoint = null; _pendingLinePoint = null;
            }
            return;
        }
        if (_tool is EditorTool.AlignX or EditorTool.AlignY)
        {
            if (_alignmentAnchor is null) _alignmentAnchor = point;
            else if (_alignmentAnchor != point) { if (_tool == EditorTool.AlignX) point.X = _alignmentAnchor.X; else point.Y = _alignmentAnchor.Y; MarkDirty(); }
        }
        _selectedPoint = point; _selectedLine = null;
    }

    private void SelectLine(RcsMapLineDto line) { if (_tool == EditorTool.Select) { _selectedLine = line; _selectedLineCodes.Clear(); _selectedLineCodes.Add(line.LineCode); _selectedPoint = null; _selectedPointCodes.Clear(); } }
    private void DeleteSelected()
    {
        if (_selectedPoint is not null) { var code = _selectedPoint.PointCode; _map.Lines.RemoveAll(x => x.FromPointCode == code || x.ToPointCode == code); _map.Points.Remove(_selectedPoint); _selectedPoint = null; }
        else if (_selectedLine is not null) { _map.Lines.Remove(_selectedLine); _selectedLine = null; }
        _mapEditorDirty = true;
    }
    private void ClearMap() { _map.Points.Clear(); _map.Lines.Clear(); ClearSelection(); _pendingLinePoint = null; _rangeSelection.Clear(); _mapEditorDirty = true; }
    private void OpenMatrix() { _tool = EditorTool.Matrix; _matrixDialogOpen = true; }
    private void CloseMatrix() { _matrixDialogOpen = false; if (_tool == EditorTool.Matrix) _tool = EditorTool.Select; }
    private void CreateMatrix()
    {
        for (var row = 0; row < Math.Clamp(_matrixRows, 1, 200); row++)
        for (var column = 0; column < Math.Clamp(_matrixColumns, 1, 200); column++)
            _map.Points.Add(new RcsMapPointDto { PointCode = NextPointCode(), PointType = _matrixType, Z = 1, X = _matrixOriginX + column * Math.Max(0, _matrixHorizontalSpacing), Y = _matrixOriginY + row * Math.Max(0, _matrixVerticalSpacing), IsEnabled = true });
        CloseMatrix(); _mapEditorDirty = true;
    }
    private async Task SaveAsync()
    {
        if (_importing) return;
        _saving = true; _message = ""; _error = false;
        try { await Api.SaveEditorMapAsync(_map); MapCache.SetSavedMap(_map); _message = "地图已保存，当前缓存已刷新。"; }
        catch (Exception ex) { _message = $"保存失败：{ex.Message}"; _error = true; }
        finally { _saving = false; }
    }
    private RcsMapPointDto? PointByCode(string code) => _map.Points.FirstOrDefault(x => x.PointCode.Equals(code, StringComparison.OrdinalIgnoreCase));
    private string NextPointCode() { var i = 1; while (_map.Points.Any(x => x.PointCode.Equals($"P-{i:0000}", StringComparison.OrdinalIgnoreCase))) i++; return $"P-{i++:0000}"; }
    private string NextLineCode() { var i = 1; while (_map.Lines.Any(x => x.LineCode.Equals($"L-{i:0000}", StringComparison.OrdinalIgnoreCase))) i++; return $"L-{i++:0000}"; }

    private object BuildMapEditorPayload() => new
    {
        Stations = _map.Points.Select(p => new { Mark = p.PointCode, PointType = p.PointType, X = p.X, Y = p.Y, StaEnable = p.IsEnabled }),
        Lines = _map.Lines.Select(l => new { l.LineCode, l.FromPointCode, l.ToPointCode, l.Direction, l.IsEnabled }),
        SelectedMark = _selectedPoint?.PointCode,
        SelectedMarks = _rangeSelection.Count > 0 ? _rangeSelection.ToArray() : (_selectedPointCodes.Count > 0 ? _selectedPointCodes.ToArray() : (_pendingLinePoint is null ? (_selectedPoint is null ? Array.Empty<string>() : new[] { _selectedPoint.PointCode }) : new[] { _pendingLinePoint.PointCode, _selectedPoint?.PointCode }.Where(x => x is not null).Cast<string>().ToArray())),
        AnchorMark = _alignmentAnchor?.PointCode,
        SelectedLineCode = _selectedLine?.LineCode,
        SelectedLineCodes = _selectedLineCodes.ToArray(),
        StationVisualScale = 0.56,
        ShowLineDirections = true,
        EnableContextMenu = true,
    };

    [JSInvokable]
    public Task OnMapEditorCanvasPoint(double x, double y)
    {
        if (_tool is EditorTool.Storage or EditorTool.Transfer or EditorTool.People or EditorTool.Sorting or EditorTool.Waypoint or EditorTool.Elevator or EditorTool.Charging)
        {
            var point = new RcsMapPointDto { PointCode = NextPointCode(), PointType = PointTypeFor(_tool), Z = 1, X = Math.Round(x, 1), Y = Math.Round(y, 1), IsEnabled = true };
            _map.Points.Add(point); _selectedPoint = point; _selectedLine = null; _tool = EditorTool.Select;
        }
        else if (_tool == EditorTool.Select) { _selectedPoint = null; _selectedLine = null; _selectedPointCodes.Clear(); _selectedLineCodes.Clear(); }
        _mapEditorDirty = true; StateHasChanged(); return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorStationClicked(string mark)
    {
        var point = PointByCode(mark); if (point is not null) { SelectPoint(point); if (_tool == EditorTool.Select) { _selectedPointCodes.Clear(); _selectedPointCodes.Add(mark); _selectedLineCodes.Clear(); } }
        _mapEditorDirty = true; StateHasChanged(); return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorLineClicked(string lineCode)
    {
        var line = _map.Lines.FirstOrDefault(x => x.LineCode.Equals(lineCode, StringComparison.OrdinalIgnoreCase));
        if (line is not null) SelectLine(line);
        _mapEditorDirty = true; StateHasChanged(); return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorBoxSelected(string[] marks)
    {
        if (_tool == EditorTool.RangeLine)
        {
            _rangeSelection.Clear();
            foreach (var mark in marks) if (PointByCode(mark) is not null) _rangeSelection.Add(mark);
            _selectedPoint = null; _selectedLine = null;
        }
        else if (_tool == EditorTool.Select)
        {
            _selectedPointCodes.Clear();
            foreach (var mark in marks) if (PointByCode(mark) is not null) _selectedPointCodes.Add(mark);
            _selectedPoint = _selectedPointCodes.Count == 1 ? PointByCode(_selectedPointCodes.First()) : null;
            _selectedLine = null; _selectedLineCodes.Clear();
        }
        _contextMenuOpen = false;
        _mapEditorDirty = true; StateHasChanged(); return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorBoxSelectedWithLines(string[] marks, string[] lineCodes)
    {
        if (_tool == EditorTool.RangeLine)
        {
            _rangeSelection.Clear();
            foreach (var mark in marks) if (PointByCode(mark) is not null) _rangeSelection.Add(mark);
            _selectedPoint = null;
            _selectedLine = null;
            _selectedPointCodes.Clear();
            _selectedLineCodes.Clear();
        }
        else if (_tool == EditorTool.Select)
        {
            _selectedPointCodes.Clear();
            foreach (var mark in marks) if (PointByCode(mark) is not null) _selectedPointCodes.Add(mark);
            _selectedLineCodes.Clear();
            foreach (var lineCode in lineCodes)
                if (_map.Lines.Any(x => x.LineCode.Equals(lineCode, StringComparison.OrdinalIgnoreCase))) _selectedLineCodes.Add(lineCode);
            _selectedPoint = _selectedPointCodes.Count == 1 ? PointByCode(_selectedPointCodes.First()) : null;
            _selectedLine = _selectedLineCodes.Count == 1 ? _map.Lines.FirstOrDefault(x => x.LineCode.Equals(_selectedLineCodes.First(), StringComparison.OrdinalIgnoreCase)) : null;
        }
        _contextMenuOpen = false;
        _mapEditorDirty = true; StateHasChanged(); return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMapEditorContextMenu(double x, double y)
    {
        if (_tool == EditorTool.Select && (_selectedPointCodes.Count > 0 || _selectedLineCodes.Count > 0))
        {
            _contextMenuX = x; _contextMenuY = y; _contextMenuOpen = true; _stationFilterMenuOpen = false; StateHasChanged();
        }
        return Task.CompletedTask;
    }

    private void CloseContextMenu() { _contextMenuOpen = false; _stationFilterMenuOpen = false; }
    private void OpenStationFilterMenu()
    {
        _selectedLineCodes.Clear();
        _selectedLine = null;
        _stationFilterMenuOpen = true;
        _mapEditorDirty = true;
        StateHasChanged();
    }
    private void KeepSelectedLines()
    {
        _selectedPointCodes.Clear();
        _rangeSelection.Clear();
        _selectedPoint = null;
        _selectedLine = _selectedLineCodes.Count == 1 ? _map.Lines.FirstOrDefault(x => _selectedLineCodes.Contains(x.LineCode)) : null;
        _stationFilterMenuOpen = false;
        _contextMenuOpen = false;
        _mapEditorDirty = true;
        StateHasChanged();
    }
    private void ClearSelection()
    {
        _selectedPoint = null; _selectedLine = null; _selectedPointCodes.Clear(); _selectedLineCodes.Clear(); _rangeSelection.Clear(); CloseContextMenu(); _mapEditorDirty = true; StateHasChanged();
    }
    private void FilterSelectedPoints(string? pointType)
    {
        if (pointType is null) { CloseContextMenu(); return; }
        _selectedPointCodes.RemoveWhere(code => !string.Equals(PointByCode(code)?.PointType, pointType, StringComparison.OrdinalIgnoreCase));
        _selectedPoint = _selectedPointCodes.Count == 1 ? PointByCode(_selectedPointCodes.First()) : null;
        _stationFilterMenuOpen = false; _contextMenuOpen = false; _mapEditorDirty = true; StateHasChanged();
    }
    private void SelectConnectedLines()
    {
        _selectedLineCodes.Clear();
        foreach (var line in _map.Lines)
            if (_selectedPointCodes.Contains(line.FromPointCode) || _selectedPointCodes.Contains(line.ToPointCode)) _selectedLineCodes.Add(line.LineCode);
        _selectedLine = _selectedLineCodes.Count == 1 ? _map.Lines.FirstOrDefault(x => _selectedLineCodes.Contains(x.LineCode)) : null;
        _contextMenuOpen = false; _mapEditorDirty = true; StateHasChanged();
    }
    private void DeleteSelectedBatch()
    {
        var codes = _selectedPointCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _map.Lines.RemoveAll(x => codes.Contains(x.FromPointCode) || codes.Contains(x.ToPointCode) || _selectedLineCodes.Contains(x.LineCode));
        _map.Points.RemoveAll(x => codes.Contains(x.PointCode));
        ClearSelection();
    }
    private void ConfirmRangeConnection()
    {
        var points = _map.Points.Where(x => _rangeSelection.Contains(x.PointCode)).ToList();
        var connectedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in points)
        {
            // 按同一行/列分别寻找四个方向上的最近站点，避免斜向连接。
            var neighbors = new[]
            {
                points.Where(x => x.X > a.X && Math.Abs(x.Y - a.Y) <= 0.5).OrderBy(x => x.X - a.X).FirstOrDefault(), // 右
                points.Where(x => x.X < a.X && Math.Abs(x.Y - a.Y) <= 0.5).OrderByDescending(x => x.X).FirstOrDefault(), // 左
                points.Where(x => x.Y > a.Y && Math.Abs(x.X - a.X) <= 0.5).OrderBy(x => x.Y - a.Y).FirstOrDefault(), // 上
                points.Where(x => x.Y < a.Y && Math.Abs(x.X - a.X) <= 0.5).OrderByDescending(x => x.Y).FirstOrDefault(), // 下
            };
            foreach (var b in neighbors.Where(x => x is not null).Cast<RcsMapPointDto>())
            {
                var pairKey = string.Compare(a.PointCode, b.PointCode, StringComparison.OrdinalIgnoreCase) < 0
                    ? $"{a.PointCode}\u001f{b.PointCode}"
                    : $"{b.PointCode}\u001f{a.PointCode}";
                if (!connectedPairs.Add(pairKey)) continue;
                if (_map.Lines.Any(x => (x.FromPointCode.Equals(a.PointCode, StringComparison.OrdinalIgnoreCase) && x.ToPointCode.Equals(b.PointCode, StringComparison.OrdinalIgnoreCase)) || (x.FromPointCode.Equals(b.PointCode, StringComparison.OrdinalIgnoreCase) && x.ToPointCode.Equals(a.PointCode, StringComparison.OrdinalIgnoreCase)))) continue;
                _map.Lines.Add(new RcsMapLineDto { LineCode = NextLineCode(), FromPointCode = a.PointCode, ToPointCode = b.PointCode, Distance = Distance(a, b), Direction = "BIDIRECTIONAL", IsEnabled = true });
            }
        }
        _rangeSelection.Clear(); _tool = EditorTool.Select; _pathToolsExpanded = false; _mapEditorDirty = true; StateHasChanged();
    }

    private Task ZoomIn() => Js.InvokeVoidAsync("grcsMapEditorZoomHost", "rcs-map-editor-host", 1.2).AsTask();
    private Task ZoomOut() => Js.InvokeVoidAsync("grcsMapEditorZoomHost", "rcs-map-editor-host", 1 / 1.2).AsTask();
    private Task ResetView() => Js.InvokeVoidAsync("grcsMapEditorResetHost", "rcs-map-editor-host").AsTask();

    private static double Distance(RcsMapPointDto a, RcsMapPointDto b) => Math.Round(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), 2);
    private static string PointTypeFor(EditorTool tool) => tool switch
    {
        EditorTool.Storage => "STORAGE",
        EditorTool.Transfer => "TRANSFER",
        EditorTool.People => "PEOPLE",
        EditorTool.Sorting => "SORTING",
        EditorTool.Elevator => "ELEVATOR",
        EditorTool.Charging => "CHARGING",
        _ => "WAYPOINT"
    };
    private enum EditorTool { Select, Storage, Transfer, People, Sorting, Waypoint, Elevator, Charging, Line, RangeLine, Matrix, AlignX, AlignY }
}
