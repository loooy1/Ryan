using System.Data;
using Backend.Shared.Infrastructure;
using Contracts.Rcs.Inventory;
using Microsoft.EntityFrameworkCore;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Application.Inventory;

/// <summary>任务确认取货和放货动作后同步更新 RCS 库存，与任务来源和类型无关。</summary>
public sealed class RcsInventoryTransferService(IDbContextFactory<GrcsDbContext> factory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedDestinations = new(StringComparer.OrdinalIgnoreCase);

    public event Action? InventoryChanged;

    /// <summary>
    /// Reserve inventory before acknowledging a task. This makes inventory failures synchronous
    /// at task_receive and prevents another newly received task from claiming the same item/slot.
    /// </summary>
    public async Task ReserveBeforeAcceptanceAsync(string taskId, string mapCode, string containerCode,
        string sourcePoint, string destinationPoint, CancellationToken token)
    {
        if (string.Equals(sourcePoint, destinationPoint, StringComparison.OrdinalIgnoreCase))
            throw new RcsInventoryPreconditionException("取货站点和放货站点不能相同。", isConflict: false);

        var destinationKey = LocationKey(mapCode, destinationPoint);
        await _gate.WaitAsync(token);
        try
        {
            if (_reservations.TryGetValue(taskId, out var existing))
            {
                if (existing.MapCode == mapCode && existing.SourcePoint == sourcePoint
                    && existing.DestinationPoint == destinationPoint && existing.InstanceCode == containerCode)
                    return;
                throw new RcsInventoryPreconditionException($"任务 {taskId} 已存在不同的库存预约。", isConflict: true);
            }
            if (_reservedDestinations.Contains(destinationKey))
                throw new RcsInventoryPreconditionException($"目标站点 {destinationPoint} 已被另一搬运任务预约。", isConflict: true);

            await using var db = await factory.CreateDbContextAsync(token);
            var item = await db.Set<RcsInventoryInstanceRow>().FirstOrDefaultAsync(x =>
                (x.ItemType == RcsInventoryItemTypes.Pallet || x.ItemType == RcsInventoryItemTypes.Cargo)
                && x.InstanceCode == containerCode
                && x.ParentInstanceCode == "" && x.MapCode == mapCode && x.PointCode == sourcePoint
                && x.Status == "AVAILABLE", token);
            if (item is null)
                throw new RcsInventoryPreconditionException(
                    $"起点 {sourcePoint} 没有可取的独立货物或托盘 {containerCode}。", isConflict: false);

            var itemKey = ItemKey(item.ItemType, item.InstanceCode);
            if (_reservedItems.Contains(itemKey))
                throw new RcsInventoryPreconditionException($"库存 {containerCode} 已被另一任务预约。", isConflict: true);
            var destinationOccupied = await db.Set<RcsInventoryInstanceRow>().AnyAsync(x =>
                x.MapCode == mapCode && x.PointCode == destinationPoint && x.ParentInstanceCode == "", token);
            if (destinationOccupied)
                throw new RcsInventoryPreconditionException(
                    $"目标站点 {destinationPoint} 已有独立库存，不能放置新的货物或托盘。", isConflict: false);

            _reservations.Add(taskId, new(item.ItemType, item.InstanceCode, mapCode, sourcePoint, destinationPoint));
            _reservedItems.Add(itemKey);
            _reservedDestinations.Add(destinationKey);
        }
        finally { _gate.Release(); }
    }

    public async Task ReserveAsync(RcsInventoryMove move, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(move.FetchPointCode) || string.IsNullOrWhiteSpace(move.PutPointCode)
            || move.FetchPointCode == move.PutPointCode || string.IsNullOrWhiteSpace(move.ContainerCode))
            throw new InvalidOperationException("库存搬运任务必须包含不同起终点的一次取货和一次放货，并指定货物或托盘编码。");

        var destinationKey = LocationKey(move.MapCode, move.PutPointCode);
        await _gate.WaitAsync(token);
        try
        {
            if (_reservations.ContainsKey(move.TaskId)) return;
            if (_reservedDestinations.Contains(destinationKey))
                throw new InvalidOperationException($"目标站点 {move.PutPointCode} 已被另一搬运任务预约。");

            await using var db = await factory.CreateDbContextAsync(token);
            var item = await db.Set<RcsInventoryInstanceRow>().FirstOrDefaultAsync(x =>
                (x.ItemType == RcsInventoryItemTypes.Pallet || x.ItemType == RcsInventoryItemTypes.Cargo)
                && x.InstanceCode == move.ContainerCode
                && x.ParentInstanceCode == "" && x.MapCode == move.MapCode && x.PointCode == move.FetchPointCode
                && x.Status == "AVAILABLE", token);
            if (item is null)
                throw new InvalidOperationException($"起点 {move.FetchPointCode} 没有可取的独立货物或托盘 {move.ContainerCode}。");
            var itemKey = ItemKey(item.ItemType, item.InstanceCode);
            if (_reservedItems.Contains(itemKey))
                throw new InvalidOperationException($"库存 {move.ContainerCode} 已被另一任务预约。");
            var destinationOccupied = await db.Set<RcsInventoryInstanceRow>().AnyAsync(x =>
                x.MapCode == move.MapCode && x.PointCode == move.PutPointCode && x.ParentInstanceCode == "", token);
            if (destinationOccupied) throw new InvalidOperationException($"目标站点 {move.PutPointCode} 已有独立库存，不能放置新的货物或托盘。");

            _reservations.Add(move.TaskId, new(item.ItemType, item.InstanceCode, move.MapCode, move.FetchPointCode, move.PutPointCode));
            _reservedItems.Add(itemKey);
            _reservedDestinations.Add(destinationKey);
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyCompletedActionAsync(string taskId, string action, CancellationToken token)
    {
        if (action is not (Contracts.Rcs.Protocol.VehiclePointAction.Fetch or Contracts.Rcs.Protocol.VehiclePointAction.Put)) return;
        if (!_reservations.TryGetValue(taskId, out var reservation))
            throw new InvalidOperationException("库存预约已失效，不能提交取放货状态。");

        await using var strategyContext = await factory.CreateDbContextAsync(token);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Use a fresh context per retry so failed-attempt tracked entity state cannot masquerade
            // as a committed inventory transition on the next execution-strategy attempt.
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var item = await db.Set<RcsInventoryInstanceRow>().FirstOrDefaultAsync(x =>
                x.ItemType == reservation.ItemType && x.InstanceCode == reservation.InstanceCode, token)
                ?? throw new InvalidOperationException($"库存中找不到{KindName(reservation.ItemType)} {reservation.InstanceCode}。");
            var now = DateTime.UtcNow;
            if (action == Contracts.Rcs.Protocol.VehiclePointAction.Fetch)
            {
                // A retry may run after the first transaction committed but its acknowledgement was lost.
                // Treat the already-applied transition as success so the vehicle can continue to delivery.
                if (item.Status == "AVAILABLE" && item.MapCode == reservation.MapCode && item.PointCode == reservation.SourcePoint)
                {
                    item.Status = "IN_TRANSIT"; item.MapCode = ""; item.PointCode = ""; item.UpdatedAt = now;
                }
                else if (item.Status != "IN_TRANSIT" || item.MapCode != "" || item.PointCode != "")
                    throw new InvalidOperationException($"{KindName(reservation.ItemType)} {reservation.InstanceCode} 已不在起点，无法取货。");

                if (item.ItemType == RcsInventoryItemTypes.Pallet)
                {
                    var cargo = await db.Set<RcsInventoryInstanceRow>().Where(x => x.ItemType == RcsInventoryItemTypes.Cargo
                        && x.ParentInstanceCode == item.InstanceCode).ToListAsync(token);
                    foreach (var child in cargo)
                    {
                        child.Status = "IN_TRANSIT"; child.MapCode = ""; child.PointCode = ""; child.UpdatedAt = now;
                    }
                }
            }
            else
            {
                var alreadyDelivered = item.Status == "AVAILABLE" && item.MapCode == reservation.MapCode
                    && item.PointCode == reservation.DestinationPoint;
                if (!alreadyDelivered)
                {
                    if (item.Status != "IN_TRANSIT" || item.MapCode != "" || item.PointCode != "")
                        throw new InvalidOperationException($"{KindName(reservation.ItemType)} {reservation.InstanceCode} 不处于在途状态，无法放货。");
                    var occupied = await db.Set<RcsInventoryInstanceRow>().AnyAsync(x =>
                        x.Id != item.Id && x.MapCode == reservation.MapCode && x.PointCode == reservation.DestinationPoint
                        && x.ParentInstanceCode == "", token);
                    if (occupied) throw new InvalidOperationException($"目标站点 {reservation.DestinationPoint} 已被其他库存占用。");
                    item.Status = "AVAILABLE"; item.MapCode = reservation.MapCode;
                    item.PointCode = reservation.DestinationPoint; item.UpdatedAt = now;
                }

                if (item.ItemType == RcsInventoryItemTypes.Pallet)
                {
                    var cargo = await db.Set<RcsInventoryInstanceRow>().Where(x => x.ItemType == RcsInventoryItemTypes.Cargo
                        && x.ParentInstanceCode == item.InstanceCode).ToListAsync(token);
                    foreach (var child in cargo)
                    {
                        child.Status = "AVAILABLE"; child.MapCode = reservation.MapCode;
                        child.PointCode = reservation.DestinationPoint; child.UpdatedAt = now;
                    }
                }
            }

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        InventoryChanged?.Invoke();
    }

    public void Release(string taskId)
    {
        _gate.Wait();
        try
        {
            if (!_reservations.Remove(taskId, out var reservation)) return;
            _reservedItems.Remove(ItemKey(reservation.ItemType, reservation.InstanceCode));
            _reservedDestinations.Remove(LocationKey(reservation.MapCode, reservation.DestinationPoint));
        }
        finally { _gate.Release(); }
    }

    private static string LocationKey(string mapCode, string pointCode) => $"{mapCode}\u001f{pointCode}";
    private static string ItemKey(string itemType, string instanceCode) => $"{itemType}\u001f{instanceCode}";
    private static string KindName(string itemType) => itemType == RcsInventoryItemTypes.Pallet ? "托盘" : "货物";
    private sealed record Reservation(string ItemType, string InstanceCode, string MapCode, string SourcePoint, string DestinationPoint);
}

public sealed class RcsInventoryPreconditionException(string message, bool isConflict) : Exception(message)
{
    public bool IsConflict { get; } = isConflict;
}
