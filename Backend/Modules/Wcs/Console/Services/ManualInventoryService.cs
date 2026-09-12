using System.Text.Json;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Proxy.Services;

namespace WCSBackend.Modules.Wcs.Console.Services;

/// <summary>
/// WCS 手工入库协调器：先批量写入 WCS 储位表，再按站点顺序逐条调用 RCS 入库接口。
/// 此接口仅处理可指定储位的纯货物；托盘必须由 RCS 的 /AutoContainerEnter 创建，再同步回 WCS。
/// </summary>
public sealed class ManualInventoryService
{
    private readonly WcsInventoryStore _inventory;
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly ILogger<ManualInventoryService> _logger;
    private readonly object _codeLock = new();
    private long _lastMillisecond;
    private int _sequence;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ManualInventoryService(WcsInventoryStore inventory, GrcsHttpClient grcs,
        WcsSettingsService settings, ILogger<ManualInventoryService> logger)
    {
        _inventory = inventory;
        _grcs = grcs;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ManualInventoryEnterResult> EnterAsync(ManualInventoryEnterRequest request)
    {
        var result = new ManualInventoryEnterResult();
        var type = request.InventoryType?.Trim().ToLowerInvariant() ?? "";
        var prefix = request.Prefix?.Trim() ?? "";
        var marks = request.StationMarks.Where(mark => !string.IsNullOrWhiteSpace(mark))
            .Select(mark => mark.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (type != ManualInventoryTypes.Cargo)
        {
            result.Message = "指定储位手工入库仅支持纯货物；托盘请使用 RCS 批量生成后同步库存";
            return result;
        }
        if (marks.Count == 0) { result.Message = "未选择储位"; return result; }
        if (string.IsNullOrWhiteSpace(prefix)) { result.Message = "库存类型前缀不能为空"; return result; }
        if (request.CargoSizeId <= 0) { result.Message = "RCS CargoSizeId 必须大于 0"; return result; }

        var settings = _settings.Get();
        if (settings != null && !string.IsNullOrWhiteSpace(settings.GrcsBaseUrl) && !string.IsNullOrWhiteSpace(settings.SceneName))
        {
            // 只读预校验模型，避免错误 ID 导致 WCS 已写入、RCS 未写入。
            var (sizeQueryOk, _, sizeJson) = await _grcs.QueryCargoSizesAsync(settings.GrcsBaseUrl);
            if (!sizeQueryOk)
            {
                result.Message = "无法读取 RCS 货物尺寸模型，未写入 WCS：" + GetResponseMessage(sizeJson, "RCS 不可用");
                return result;
            }

            var knownSizes = GetCargoSizes(sizeJson);
            if (knownSizes.Any(size => size.Id == request.CargoSizeId) == false)
            {
                var names = string.Join("、", knownSizes.Select(size => $"{size.Name}(ID {size.Id})"));
                result.Message = $"RCS 不存在货物尺寸模型 ID {request.CargoSizeId}，未写入 WCS。可用模型：{(string.IsNullOrWhiteSpace(names) ? "无" : names)}";
                return result;
            }
        }

        var codes = GenerateCodes(prefix, marks.Count);
        var units = marks.Zip(codes, (mark, code) => new WcsInventoryStore.ManualStockUnit(mark, code, type)).ToList();
        var (written, message) = _inventory.TryAddManualStock(units);
        if (!written)
        {
            result.Message = message;
            return result;
        }

        result.WcsWritten = true;
        result.Message = "WCS 库存已写入，正在按站点同步 RCS";
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl) || string.IsNullOrWhiteSpace(settings.SceneName))
        {
            result.Message = "WCS 库存已写入，但未配置 RCS 地址或场景，未同步 RCS";
            result.Items = units.Select(unit => ToResult(unit, false, 0, "未配置 RCS 地址或场景")).ToList();
            return result;
        }

        foreach (var unit in units)
        {
            var payload = new RcsCargoEnterRequest
            {
                Code = unit.Code,
                CargoSizeId = request.CargoSizeId,
                HomeStationMark = unit.Mark,
                HomeStationScene = settings.SceneName,
                HomeCargoAreaName = "",
            };
            var (httpOk, statusCode, json) = await _grcs.EnterCargoAsync(settings.GrcsBaseUrl, payload);
            var rcsSynced = httpOk && IsRcsSuccess(json);
            var responseMessage = GetResponseMessage(json, rcsSynced ? "同步成功" : "RCS 入库失败");
            result.Items.Add(ToResult(unit, rcsSynced, statusCode, responseMessage));
            if (!rcsSynced)
                _logger.LogWarning("手工入库 RCS 同步失败：{Station} {Code} HTTP {StatusCode}: {Message}",
                    unit.Mark, unit.Code, statusCode, responseMessage);
        }

        var failed = result.Items.Count(item => !item.RcsSynced);
        result.Message = failed == 0
            ? $"已写入 WCS 并同步 RCS：{result.Items.Count} 条"
            : $"WCS 已写入 {result.Items.Count} 条；RCS 同步成功 {result.Items.Count - failed} 条，失败 {failed} 条";
        return result;
    }

    /// <summary>打开库存地图时读取一次 RCS 的在途托盘位置；不保留定时器。</summary>
    public async Task<List<TransitPalletPositionDto>> GetTransitPalletPositionsAsync()
    {
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl)) return [];
        var snapshots = new List<TransitPalletPositionDto>();
        foreach (var unit in _inventory.GetTransitUnits().Where(unit => !string.IsNullOrWhiteSpace(unit.PalletCode)))
        {
            var palletCode = unit.PalletCode!;
            var (ok, _, json) = await _grcs.QueryCargoInventoryAsync(settings.GrcsBaseUrl, settings.SceneName, palletCode);
            if (!ok) continue;
            try
            {
                var record = JsonSerializer.Deserialize<CargoQueryResult>(json, JsonOptions)?.Data?.Records
                    ?.FirstOrDefault(item => string.Equals(item.Code, palletCode, StringComparison.OrdinalIgnoreCase));
                if (record == null) continue;
                snapshots.Add(new TransitPalletPositionDto
                {
                    TaskId = unit.TaskId,
                    PalletCode = palletCode,
                    SourceMark = unit.SourceMark,
                    RobotId = record.RobotId ?? "",
                    CurrentStationCode = record.CurrentStationCode ?? "",
                    CurrentLocation = record.CurrentLocation,
                    IsLoaded = record.IsLoaded,
                });
            }
            catch (JsonException) { }
        }
        return snapshots;
    }

    private List<string> GenerateCodes(string prefix, int count)
    {
        lock (_codeLock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now != _lastMillisecond) { _lastMillisecond = now; _sequence = 0; }
            var start = _sequence;
            _sequence += count;
            var timeHex = now.ToString("X");
            return Enumerable.Range(start, count).Select(index => $"{prefix}{timeHex}{index:X4}").ToList();
        }
    }

    private static ManualInventoryEnterItemResult ToResult(WcsInventoryStore.ManualStockUnit unit,
        bool rcsSynced, int statusCode, string message) => new()
        {
            StationMark = unit.Mark,
            Code = unit.Code,
            InventoryType = unit.InventoryType,
            WcsWritten = true,
            RcsSynced = rcsSynced,
            RcsStatusCode = statusCode,
            Message = message,
        };

    private static string GetResponseMessage(string json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? fallback;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(json) ? fallback : json.Length > 300 ? json[..300] + "…" : json;
    }

    /// <summary>RCS 的 IMessageResult 业务失败可能仍使用 HTTP 200，需同时识别 success=false。</summary>
    private static bool IsRcsSuccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var property = doc.RootElement.EnumerateObject()
                .FirstOrDefault(item => string.Equals(item.Name, "success", StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return property.Value.GetBoolean();
        }
        catch (JsonException) { return false; }
        return true;
    }

    private static List<RcsCargoSizeDto> GetCargoSizes(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array) return [];
            return JsonSerializer.Deserialize<List<RcsCargoSizeDto>>(records.GetRawText(), JsonOptions) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private sealed class RcsCargoEnterRequest
    {
        public string Code { get; set; } = "";
        public int CargoSizeId { get; set; }
        public string HomeStationMark { get; set; } = "";
        public string HomeStationScene { get; set; } = "";
        public string HomeCargoAreaName { get; set; } = "";
    }
}
