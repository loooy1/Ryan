using System.IO.Compression;
using System.Text.Json;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Dashboard.Modules.WcsSimulator.Models;
using Dashboard.Modules.WcsSimulator.Services;
using Contracts.Dtos;

namespace Dashboard.Modules.WcsSimulator.Pages;

public partial class MapReader
{
    internal bool IsSectionCollapsed(string key) => IsCollapsed(key);

    internal string _warehouse = "Show";                  // 场景名称（持久化到浏览器）
    internal string _grcsBaseUrl = "http://localhost:8224"; // GRCS 地址（持久化，所有模块共用）
    internal string _wcsBaseUrl = "http://localhost:8230";  // WCS 后端地址（持久化，所有模块共用）
    internal bool _warehouseSaved;                        // 保存成功提示
    internal bool _mapLoaded;                              // 地图读取成功提示
    internal string _mapError = "";                        // 接口读取失败提示
    internal List<MapStationLite> _stations = [];       // 全部站点列表（精简数据，可持久化）
    internal int _pathsCount;                            // 连线数量
    internal bool _loading;                              // 解析中标志
    internal readonly List<MapStationLite> _createdMapStations = [];
    internal readonly HashSet<string> _selectedCreatedMarks = new(StringComparer.OrdinalIgnoreCase);
    internal MapStationLite? _selectedCreatedStation;
    internal MapStationLite? _alignmentAnchor;
    internal string _selectedCreatedCargoAreas = "";
    internal string _mapBuilderTool = "select";
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
    internal DotNetObjectReference<MapReader>? _mapEditorRef;
    internal bool _mapEditorDirty = true;

    internal void SetMapBuilderTool(string tool)
    {
        _mapBuilderTool = tool;
        if (tool is not "align-x" and not "align-y") _alignmentAnchor = null;
    }
    internal void MarkMapEditorDirty() => _mapEditorDirty = true;
    internal void SaveCreatedMapPlaceholder() => ShowFeedback("ℹ️ 当前仅完成界面，保存到库存地图功能暂未实现");
    internal string NextCreatedStationMark()
    {
        while (_createdMapStations.Any(s => s.Mark.Equals($"S{_nextCreatedStationIndex:000}", StringComparison.OrdinalIgnoreCase)))
            _nextCreatedStationIndex++;
        return $"S{_nextCreatedStationIndex++:000}";
    }
    internal object BuildMapEditorPayload() => new
    {
        Stations = _createdMapStations.Select(s => new { s.Mark, s.StationType, s.X, s.Y, s.StaEnable }),
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
    internal Task ResetMapZoom() => Js.InvokeVoidAsync("grcsMapEditorResetHost", "map-create-canvas-host").AsTask();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _mapEditorRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("grcsMapEditorMount", "map-create-canvas-host", _mapEditorRef,
                JsonSerializer.Serialize(BuildMapEditorPayload()));
            _mapEditorDirty = false;
        }
        else if (_mapEditorDirty)
        {
            await Js.InvokeVoidAsync("grcsMapEditorUpdate", "map-create-canvas-host",
                JsonSerializer.Serialize(BuildMapEditorPayload()));
            _mapEditorDirty = false;
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
    // ── 筛选状态（随缓存一起保存恢复）──
    internal int _typeFilter;                            // 类型筛选（0=全部）
    internal string _search = "";                        // 关键词
    internal string _enableFilter = "";                  // 启用状态筛选（空=全部）

    /// <summary>筛选后的站点。</summary>
    internal List<MapStationLite> _filtered => _stations.Where(s =>
        (_typeFilter == 0 || (s.StationType & _typeFilter) != 0)
        && (_enableFilter == "" || s.StaEnable.ToString().ToLower() == _enableFilter)
        && (_search == ""
            || s.Mark.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || s.CargoAreas.Any(a => a.Contains(_search, StringComparison.OrdinalIgnoreCase)))
    ).ToList();

    internal static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── 卡片折叠状态（持久化到 localStorage 键 grcs_mr_collapsed，切走再回来保持；
    //    基类 PageStateBase 提供，本页默认全折叠、不恢复上次展开状态）──
    protected override string CollapsedStoreKey => "grcs_mr_collapsed";
    internal Task ToggleFile() => Toggle("mr_file");
    internal Task ToggleSettings() => Toggle("mr_settings");
    internal Task ToggleOverview() => Toggle("mr_overview");
    /// <summary>
    /// 初始化：从内存缓存恢复上次的地图数据（grcs_map_stations）与界面状态
    /// （同步读取，无 JS 边界）；恢复场景名与两个后端地址（共享键
    /// grcs_warehouse / grcs_grcs_url / grcs_wcs_url）；
    /// 折叠状态默认全部折叠、不恢复上次展开状态，避免进来即渲染大数据表。
    /// </summary>
    // 页面初始化：从内存缓存恢复上次的地图数据与界面状态（同步读取，无 JS 边界）
    protected override async Task OnInitializedAsync()
    {
        try
        {
            var json = LocalStore["grcs_map_stations"];
            if (!string.IsNullOrEmpty(json) && json != "null")
            {
                var cache = JsonSerializer.Deserialize<MapStationCache>(json, JsonOpts);
                if (cache?.Stations is { Count: > 0 })
                {
                    _stations = cache.Stations;
                    _pathsCount = cache.PathsCount;
                    _typeFilter = cache.Filter?.TypeFilter ?? 0;
                    _search = cache.Filter?.Search ?? "";
                    _enableFilter = cache.Filter?.EnableFilter ?? "";
                }
            }
        }
        catch { /* 读取缓存失败时忽略，重新选择文件即可 */ }

        // 恢复 localStorage 状态（内存缓存，同步读取）
        try
        {
            if (V("grcs_wcs_url") is string wcs) _wcsBaseUrl = wcs;
            if (V("grcs_grcs_url") is string grcs) _grcsBaseUrl = grcs;
            if (V("grcs_warehouse") is string wh && !string.IsNullOrEmpty(wh)) _warehouse = wh;
            // 切到模块时始终折叠，不恢复之前展开状态
        }
        catch { }

        // 系统设置以后端为准（/api/wcs/auto/settings，SQLite 持久化；未保存用默认值）
        try
        {
            var s = await WcsApi.GetAsync<WcsSettingsDto>("/api/wcs/auto/settings");
            if (s != null)
            {
                if (!string.IsNullOrWhiteSpace(s.GrcsBaseUrl)) _grcsBaseUrl = s.GrcsBaseUrl;
                if (!string.IsNullOrWhiteSpace(s.SceneName)) _warehouse = s.SceneName;
            }
        }
        catch { }
    }

    /// <summary>
    /// 从 GRCS 下载地图 zip（经 WCS 后端代理 /api/wcs/grcs/map，场景名按系统设置），
    /// 流式打开 zip 找到 map.json 条目（兼容根目录或嵌套路径），
    /// 解析成功后写入 localStorage 缓存；失败清空站点并展示错误。
    /// WCS/GRCS 后端未连接时由 WcsApiClient 统一弹窗告警。
    /// </summary>
    internal async Task LoadFromApi()
    {
        _loading = true;
        try
        {
            var (ok, error, bytes) = await WcsApi.GetMapZipAsync();
            if (!ok || bytes == null)
            {
                _stations = [];
                _mapError = error;
                return;
            }
            using var zipStream = new MemoryStream(bytes);
            using var zip = new ZipArchive(zipStream);
            var entry = zip.GetEntry("map.json") ?? zip.Entries.FirstOrDefault(e => e.Name == "map.json");
            if (entry == null) throw new Exception("zip 中未找到 map.json");
            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            var content = await reader.ReadToEndAsync();
            await ParseMapContent(content);
            _ = ShowMapLoaded();
        }
        catch (Exception ex)
        {
            _stations = [];
            _mapError = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>读取用户本地选择的 map.json 文件（上限 100MB）并解析，成功同样写入缓存。</summary>
    internal async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file == null) return;

        _loading = true;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024));
            var content = await reader.ReadToEndAsync();
            await ParseMapContent(content);
            _ = ShowMapLoaded();
        }
        catch (Exception ex)
        {
            _stations = [];
            await Js.InvokeVoidAsync("alert", $"地图文件解析失败：{ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 解析 map.json（MapParseService 纯转换），重置筛选条件，
    /// 并立即调用 SaveState 写入 localStorage 缓存供其他页面复用。
    /// </summary>
    internal async Task ParseMapContent(string content)
    {
        var (stations, pathsCount) = MapParseService.Parse(content);
        _stations = stations;
        _pathsCount = pathsCount;
        _typeFilter = 0;
        _search = "";
        _enableFilter = "";
        _mapError = "";
        await SaveState();
    }

    internal async Task ShowMapLoaded()
    {
        _mapLoaded = true;
        StateHasChanged();
        await Task.Delay(2000);
        _mapLoaded = false;
        StateHasChanged();
    }

    /// <summary>
    /// 保存当前界面状态：本地 localStorage（本页与其余读旧键的页面立即用）+ 上传到后端
    /// （/api/wcs/map/upload，Skill E：自动化/其他标签页/换浏览器共用后端这一份）。
    /// </summary>
    internal async Task SaveState()
    {
        var cache = new MapStationCache
        {
            SavedAt = DateTime.Now.ToString("HH:mm:ss"),
            PathsCount = _pathsCount,
            Stations = _stations,
            Filter = new MapReaderFilterState
            {
                TypeFilter = _typeFilter,
                Search = _search,
                EnableFilter = _enableFilter,
            }
        };
        await LocalStore.SetAsync(Js, "grcs_map_stations", JsonSerializer.Serialize(cache));
        // 后端单一数据源：解析成功（非空站点集）即上传，自动化/其他标签页从 /api/wcs/map 读取
        if (_stations.Count > 0)
        {
            _ = WcsApi.PostAsync("/api/wcs/map/upload", new MapUploadPayload
            {
                SavedAt = cache.SavedAt,
                PathsCount = _pathsCount,
                Stations = _stations,
            });
        }
    }

    internal void SetTypeFilter(int bit)
    {
        _typeFilter = bit;
        _ = SaveState();
    }

    internal void OnSearchInput(ChangeEventArgs e)
    {
        _search = e.Value?.ToString() ?? "";
    }

    internal void OnEnableFilterChange(ChangeEventArgs e)
    {
        _enableFilter = e.Value?.ToString() ?? "";
        _ = SaveState();
    }

    /// <summary>负载状态位 → 中文（1=仅带载 2=仅空载 其他=全部）。</summary>
    internal static string LoadStateText(int state) => state switch
    {
        1 => "仅带载",
        2 => "仅空载",
        _ => "全部"
    };

    /// <summary>保存系统设置（场景名 + GRCS 地址 + WCS 后端地址）：
    /// 写入后端 /api/wcs/auto/settings（SQLite，全站 GRCS 调用与场景名的唯一数据源）
    /// + 共享 localStorage 键兜底；保存后重读一次刷新显示。</summary>
    internal async Task SaveWarehouse()
    {
        await WcsApi.PutAsync<object, object>("/api/wcs/auto/settings", new { grcsBaseUrl = _grcsBaseUrl, sceneName = _warehouse });
        await LocalStore.SetAsync(Js, "grcs_warehouse", _warehouse);
        await SaveAddresses();
        // 保存后重读一次：后端为准（未保存过就用默认值）
        try
        {
            var s = await WcsApi.GetAsync<WcsSettingsDto>("/api/wcs/auto/settings");
            if (s != null)
            {
                if (!string.IsNullOrWhiteSpace(s.GrcsBaseUrl)) _grcsBaseUrl = s.GrcsBaseUrl;
                if (!string.IsNullOrWhiteSpace(s.SceneName)) _warehouse = s.SceneName;
            }
        }
        catch { }
        _warehouseSaved = true;
        StateHasChanged();
        await Task.Delay(2000);
        _warehouseSaved = false;
        StateHasChanged();
    }

    /// <summary>GRCS / WCS 两个后端地址写入各自的共享 localStorage 键。</summary>
    internal async Task SaveAddresses()
    {
        await LocalStore.SetAsync(Js, "grcs_grcs_url", _grcsBaseUrl);
        await LocalStore.SetAsync(Js, "grcs_wcs_url", _wcsBaseUrl);
    }

    /// <summary>复制站点编码到剪贴板（调用 wwwroot 里注册的 grcsCopyText JS 函数）。</summary>
    internal async Task CopyMark(string? mark)
    {
        if (string.IsNullOrEmpty(mark)) return;
        await Js.InvokeVoidAsync("grcsCopyText", mark);
    }
}



