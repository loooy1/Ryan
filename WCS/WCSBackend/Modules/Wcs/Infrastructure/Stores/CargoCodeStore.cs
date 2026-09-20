using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class CargoCodeStore
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private Dictionary<string, string> _map = [];

    public CargoCodeStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "cargo_codes");
            if (!string.IsNullOrEmpty(json))
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Opts) ?? [];
        }
        catch { }
    }

    public string Ensure(string seg1TaskId)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(seg1TaskId, out var existing)) return existing;
            var code = "SimCargo_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper();
            _map[seg1TaskId] = code;
            KvAccess.Set(_uow, "cargo_codes", JsonSerializer.Serialize(_map));
            return code;
        }
    }
}

/// <summary>
/// 信号确认状态（由 task_records 中的 SIGNAL_* 阶段派生）。
/// Set 是幂等抢占：新插入返回 true（claimed），已存在返回 false——前端据此
/// 在发信号前抢占，防止多标签页对同一任务重复发送 WCS 信号。
/// </summary>
