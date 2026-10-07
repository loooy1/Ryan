using Contracts.Rcs.Map;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Maps;

/// <summary>串行化地图保存与运行时快照刷新，保证同一次发布操作有明确入口。</summary>
public sealed class RcsMapPublicationService(RcsMapStore store, RcsMapCache cache)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SaveAsync(RcsMapEditorDto map, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await store.SaveAsync(map, token);
            await cache.ReloadAsync(token);
        }
        finally { _gate.Release(); }
    }
}
