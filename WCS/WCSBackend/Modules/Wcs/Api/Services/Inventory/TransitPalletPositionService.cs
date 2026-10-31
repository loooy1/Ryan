using System.Text.Json;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Proxy.Services;

namespace WCSBackend.Modules.Wcs.Console.Services;

/// <summary>读取库存地图上仍在途托盘的位置快照。</summary>
public sealed class TransitPalletPositionService
{
    private readonly WcsInventoryStore _inventory;
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TransitPalletPositionService(WcsInventoryStore inventory, GrcsHttpClient grcs, WcsSettingsService settings)
    {
        _inventory = inventory;
        _grcs = grcs;
        _settings = settings;
    }

    public async Task<List<TransitPalletPositionDto>> GetTransitPalletPositionsAsync()
    {
        if (_grcs.GrcsOnline == false) return [];
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
}