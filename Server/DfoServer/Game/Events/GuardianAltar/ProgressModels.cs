using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal sealed record GuardianAltarHeader(
        int RidableId, int FreeTickets, int SecondaryTickets,
        int StageStars, int OtherStars, int AvailableStars,
        short SurvivalBestPhase, short StarResetCount, uint Reserved28,
        byte MenuHintFlag);

    internal sealed record GuardianAltarStage(int DungeonId, int Progress);
    internal sealed record GuardianAltarPurchase(int ShopType, int ResourceId,
        short Level, short Price);

    internal readonly record struct GuardianAltarPurchaseRequest(
        int RidableId, int ShopType, int ResourceId, short StartLevel, short TargetLevel)
    {
        internal GuardianAltarPurchaseRequest(int ridableId, int shopType, int resourceId, short targetLevel)
            : this(ridableId, shopType, resourceId, targetLevel, targetLevel)
        {
        }
    }

    internal enum GuardianAltarPurchaseOutcome
    {
        Success,
        Closed,
        InsufficientStars,
        CannotPurchase,
    }

    internal sealed record GuardianAltarPurchasePlan(
        GuardianAltarPurchase Previous, GuardianAltarPurchase Updated, int Cost)
    {
        internal bool IsReplay => Previous == Updated;
    }

    internal sealed record GuardianAltarPurchaseResult(
        GuardianAltarPurchaseOutcome Outcome, GuardianAltarProgressSnapshot Progress,
        short PreviousLevel, short NewLevel, bool Changed, string RejectionReason = null);
    internal sealed record GuardianAltarSlot(byte Locked, int SlotType,
        byte SlotIndex, int ResourceId);

    internal readonly record struct GuardianAltarSlotRequest(
        int RidableId, bool Equip, int SlotType, byte SlotIndex, int ResourceId);

    internal sealed record GuardianAltarSlotPlan(GuardianAltarSlot Previous, GuardianAltarSlot Updated)
    {
        internal bool IsReplay => Previous == Updated;
    }

    internal sealed record GuardianAltarSlotResult(
        bool Accepted, GuardianAltarProgressSnapshot Progress, GuardianAltarSlot Slot,
        bool Changed, string RejectionReason = null);
    internal sealed record GuardianAltarAchievement(int AchievementId, byte Claimed);

    // Domain values, never an opaque wire blob. A snapshot is obtained in the
    // same transaction that initializes/refills the character-owned record.
    internal sealed class GuardianAltarProgressSnapshot
    {
        internal GuardianAltarProgressSnapshot(int characterId, GuardianAltarHeader header,
            IEnumerable<GuardianAltarStage> stages, IEnumerable<GuardianAltarPurchase> purchases,
            IEnumerable<GuardianAltarSlot> slots, IEnumerable<GuardianAltarAchievement> achievements)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            CharacterId = characterId;
            Header = header ?? throw new ArgumentNullException(nameof(header));
            Stages = Array.AsReadOnly(stages.OrderBy(x => x.DungeonId).ToArray());
            Purchases = Array.AsReadOnly(purchases.OrderBy(x => x.ShopType).ThenBy(x => x.ResourceId).ToArray());
            Slots = Array.AsReadOnly(slots.OrderBy(x => x.SlotType).ThenBy(x => x.SlotIndex).ToArray());
            Achievements = Array.AsReadOnly(achievements.OrderBy(x => x.AchievementId).ToArray());
        }

        internal int CharacterId { get; }
        internal GuardianAltarHeader Header { get; }
        internal IReadOnlyList<GuardianAltarStage> Stages { get; }
        internal IReadOnlyList<GuardianAltarPurchase> Purchases { get; }
        internal IReadOnlyList<GuardianAltarSlot> Slots { get; }
        internal IReadOnlyList<GuardianAltarAchievement> Achievements { get; }
    }

    internal interface IGuardianAltarProgressService
    {
        GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow,
            Func<bool> isCurrent = null);
    }

    // The same progress owner supplies this capability. Keeping the interface
    // narrow does not make menu-only test/adaptation providers authorize buys.
    internal interface IGuardianAltarPurchaseService
    {
        GuardianAltarPurchaseResult Buy(int characterId, int accountId,
            GuardianAltarPurchaseRequest request, Func<bool> isCurrent = null);
    }

    // This capability belongs to the existing durable progress owner, not
    // to combat runtime or a second menu cache.
    internal interface IGuardianAltarSlotService
    {
        GuardianAltarSlotResult SetSlot(int characterId, int accountId,
            GuardianAltarSlotRequest request, Func<bool> isCurrent = null);
    }

    internal static class GuardianAltarProgressValidator
    {
        internal static void Validate(GuardianAltarProgressSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var h = snapshot.Header;
            if (h.RidableId != GuardianAltarMenuDefinition.DefaultRidableId
                || h.FreeTickets < 0 || h.SecondaryTickets < 0
                || (long)h.FreeTickets + h.SecondaryTickets > int.MaxValue
                || h.StageStars < 0 || h.OtherStars < 0 || h.AvailableStars < 0
                || (long)h.StageStars + h.OtherStars > int.MaxValue
                || h.AvailableStars > (long)h.StageStars + h.OtherStars
                || h.SurvivalBestPhase < 0 || h.StarResetCount < 0 || h.MenuHintFlag > 1
                || snapshot.Stages.Count == 0
                || snapshot.Stages.Any(x => x.DungeonId <= 0 || x.Progress < -1)
                || snapshot.Stages.Select(x => x.DungeonId).Distinct().Count() != snapshot.Stages.Count
                || snapshot.Purchases.Any(x => x.ShopType < 0 || x.ShopType > 2
                    || x.ResourceId <= 0 || x.Level < 0 || x.Price < 0)
                || snapshot.Purchases.Select(x => (x.ShopType, x.ResourceId)).Distinct().Count() != snapshot.Purchases.Count
                || snapshot.Slots.Count != 12
                || snapshot.Slots.Any(x => x.Locked > 1 || x.SlotType < 1 || x.SlotType > 2
                    || x.SlotIndex > 5 || x.ResourceId < 0)
                || snapshot.Slots.Select(x => (x.SlotType, x.SlotIndex)).Distinct().Count() != 12
                || snapshot.Slots.Any(x => x.ResourceId > 0 && (x.Locked != 0
                    || !snapshot.Purchases.Any(p => p.ShopType == x.SlotType - 1
                        && p.ResourceId == x.ResourceId && p.Level > 0)))
                || snapshot.Achievements.Any(x => x.AchievementId <= 0 || x.Claimed > 1)
                || snapshot.Achievements.Select(x => x.AchievementId).Distinct().Count() != snapshot.Achievements.Count)
                throw new InvalidOperationException("Guardian menu snapshot is inconsistent.");
        }
    }
}
