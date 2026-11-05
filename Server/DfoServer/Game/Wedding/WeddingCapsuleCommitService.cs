using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Wedding
{
    internal enum WeddingCapsuleAction { Make, Give, Take }
    internal sealed record WeddingCapsuleCommitResult(int Error, WeddingCapsuleState Actor, WeddingCapsuleState Partner,
        int PartnerId = 0, int Granted = 0)
    {
        internal bool Success => Error == 0;
        internal static WeddingCapsuleCommitResult Failed(int code) => new(code, new(), new());
    }

    internal sealed class WeddingCapsuleCommitService
    {
        private readonly WeddingCapsuleCatalog _catalog;
        internal WeddingCapsuleCommitService(WeddingCapsuleCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        // Caller holds both character transition gates, including an offline spouse's gate.
        // Only Take touches inventory; Give writes the spouse's durable stock, never an offline inventory.
        internal WeddingCapsuleCommitResult Commit(InventoryLease actor, int partnerId, WeddingCapsuleAction action, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (actor == null || partnerId <= 0 || partnerId == actor.CharacterId || !Enum.IsDefined(action))
                return WeddingCapsuleCommitResult.Failed(-1);
            lock (actor.SyncRoot)
            {
                if (!Current(actor) || !stillCurrent()) return WeddingCapsuleCommitResult.Failed(-1);
                if (!InventoryPersistenceService.SaveDirty(actor)) return WeddingCapsuleCommitResult.Failed(-1);
                InventoryService baseline = null;
                bool inventoryMutated = false;
                try
                {
                    var database = actor.Inventory.Database;
                    using var connection = database.OpenConnection();
                    if (action == WeddingCapsuleAction.Take)
                        baseline = InventoryService.LoadFromDb(connection, actor.CharacterId, actor.AccountId, database);
                    using var transaction = connection.BeginTransaction();
                    using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
                    var owner = WeddingRepository.LoadActor(connection, transaction, actor.CharacterId);
                    var spouse = WeddingRepository.LoadActor(connection, transaction, partnerId);
                    Require(owner != null && spouse != null && !owner.Deleted && !spouse.Deleted && owner.AccountId == actor.AccountId, -1);
                    var wedding = WeddingRepository.Load(connection, transaction, actor.CharacterId);
                    var partner = WeddingRepository.Load(connection, transaction, partnerId);
                    Require(wedding.Status == WeddingStatus.Married && WeddingRules.IsReciprocal(wedding, partner), -1);
                    var a = WeddingCapsuleRepository.Load(connection, transaction, wedding.PairId, actor.CharacterId);
                    var b = WeddingCapsuleRepository.Load(connection, transaction, wedding.PairId, partnerId);
                    int granted = 0;
                    switch (action)
                    {
                        case WeddingCapsuleAction.Make:
                            Require(a.Progress >= _catalog.MaximumGauge, -4);
                            Require(a.Own < _catalog.MaximumOwn, -5);
                            a = a with { Progress = a.Progress - _catalog.MaximumGauge, Own = checked((byte)(a.Own + 1)) };
                            break;
                        case WeddingCapsuleAction.Give:
                            Require(a.Own > 0, -4);
                            Require(a.Own <= _catalog.MaximumTaken - b.Taken, -6);
                            b = b with { Taken = checked((byte)(b.Taken + a.Own)) };
                            a = a with { Own = 0 };
                            break;
                        case WeddingCapsuleAction.Take:
                            Require(a.Taken > 0, -1);
                            inventoryMutated = true;
                            Require(InventoryRewardGrantService.TryCreateAndInsert(actor.Inventory, _catalog.ItemId,
                                ItemCreateReason.Unknown, a.Taken, out _), -6);
                            granted = a.Taken;
                            a = a with { Taken = 0 };
                            break;
                    }
                    WeddingCapsuleRepository.Save(connection, transaction, wedding.PairId, actor.CharacterId, a);
                    if (action == WeddingCapsuleAction.Give)
                        WeddingCapsuleRepository.Save(connection, transaction, wedding.PairId, partnerId, b);
                    Require(Current(actor) && stillCurrent(), -1);
                    if (inventoryMutated) Require(InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, actor), -1);
                    transaction.Commit();
                    if (inventoryMutated) actor.Inventory.ClearDirtyState();
                    return new(0, a, b, partnerId, granted);
                }
                catch (Exception ex)
                {
                    if (inventoryMutated && baseline != null) InventoryContext.TryReplaceCurrentLease(actor, baseline, out _);
                    if (ex is CapsuleFailure failure) return WeddingCapsuleCommitResult.Failed(failure.Code);
                    FileLogger.Log($"[WeddingCapsule] transaction failed cid={actor.CharacterId}/{partnerId} action={action}: {ex}");
                    return WeddingCapsuleCommitResult.Failed(-1);
                }
            }
        }

        private static bool Current(InventoryLease lease) => InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);
        private static void Require(bool value, int error) { if (!value) throw new CapsuleFailure(error); }
        private sealed class CapsuleFailure : Exception
        {
            internal int Code { get; }
            internal CapsuleFailure(int code) => Code = code;
        }
    }
}
