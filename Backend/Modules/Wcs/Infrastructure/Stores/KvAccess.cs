using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
internal static class KvAccess
{
    public static string? Get(IUnitOfWorkFactory uowFactory, string key)
    {
        using var uow = uowFactory.Create();
        return uow.Repository<KvRow>().FindAsync(key).GetAwaiter().GetResult()?.Value;
    }

    public static void Set(IUnitOfWorkFactory uowFactory, string key, string value)
    {
        using var uow = uowFactory.Create();
        var repo = uow.Repository<KvRow>();
        var row = repo.FindAsync(key).GetAwaiter().GetResult();
        if (row == null) repo.AddAsync(new KvRow { Key = key, Value = value }).GetAwaiter().GetResult();
        else row.Value = value;
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}

/// <summary>站点池缓存（地图上传/GRCS 拉取后持久化，重启不丢）。Singleton。</summary>
