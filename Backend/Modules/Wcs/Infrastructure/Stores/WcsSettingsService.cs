using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class WcsSettingsService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private WcsSettingsDto _settings = new();

    public WcsSettingsService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "wcs_settings");
            if (!string.IsNullOrEmpty(json))
                _settings = new KvRow { Value = json }.Adapt<WcsSettingsDto>();
        }
        catch { }
    }

    public WcsSettingsDto Get() { lock (_lock) { return new WcsSettingsDto { GrcsBaseUrl = _settings.GrcsBaseUrl, SceneName = _settings.SceneName }; } }

    public void Set(WcsSettingsDto s)
    {
        lock (_lock)
        {
            _settings.GrcsBaseUrl = string.IsNullOrWhiteSpace(s.GrcsBaseUrl) ? _settings.GrcsBaseUrl : s.GrcsBaseUrl.Trim();
            _settings.SceneName = s.SceneName ?? "";
            KvAccess.Set(_uow, "wcs_settings", _settings.Adapt<KvRow>().Value);
        }
    }
}

/// <summary>归巢模式配置（地图框选巢区站点 Mark 列表，内存 + SQLite 持久化，与 auto_range 相互独立）。Singleton。</summary>
