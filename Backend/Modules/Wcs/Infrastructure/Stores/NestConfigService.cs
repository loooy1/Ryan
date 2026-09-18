using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class NestConfigService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private NestConfigDto _config = new();

    public NestConfigService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "nest_config");
            if (!string.IsNullOrEmpty(json))
                _config = new KvRow { Value = json }.Adapt<NestConfigDto>();
        }
        catch { }
    }

    public NestConfigDto Get()
    {
        lock (_lock) { return new NestConfigDto { Marks = _config.Marks.ToList() }; }
    }

    public void Set(NestConfigDto config)
    {
        lock (_lock)
        {
            _config.Marks = (config.Marks ?? [])
                .Select(m => m?.Trim() ?? "")
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            KvAccess.Set(_uow, "nest_config", _config.Adapt<KvRow>().Value);
        }
    }
}

/// <summary>段1 任务 → 货物码映射（入库段2 与信号放行共用同一码）。Singleton。</summary>
