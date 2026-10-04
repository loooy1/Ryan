using Contracts.Rcs.Map;

namespace RCSBackend.Modules.Rcs.Infrastructure.Stores;

/// <summary>RCS 进程内地图缓存。数据库只在启动或显式刷新时读取，算法持有当前快照引用。</summary>
public sealed class RcsMapCache
{
    private readonly RcsMapStore _store;
    private readonly object _gate = new();
    private RcsMapSnapshot? _current;

    public RcsMapCache(RcsMapStore store) => _store = store;
    public RcsMapSnapshot? Current { get { lock (_gate) return _current; } }

    public async Task<RcsMapSnapshot?> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.LoadActiveSnapshotAsync(cancellationToken);
        lock (_gate) _current = snapshot;
        return snapshot;
    }
}
