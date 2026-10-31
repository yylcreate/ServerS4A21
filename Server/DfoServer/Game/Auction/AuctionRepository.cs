using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// 拍卖行数据层。上架/搜索/一口价/下架/到期全部走 SQLite 事务；
    /// 道具托管复用邮件系统的 ItemCore 快照结构，结算退回直接构造系统邮件附件。
    /// 约定（ClockService.md）：上架单必须跨进程重启存活，存绝对到期时间。
    /// </summary>
    public sealed partial class AuctionRepository
    {
        private readonly string _connectionString;

        /// <summary>
        /// 86JP 金币道具模板 id（0x28EB7D）。客户端「金币寄售」发 0x00B7 上架时，
        /// 请求体 [4..7] 的 itemId 就是这个值；金币市场使用独立的 28B 请求分支。
        /// 金币存在虚拟槽 0（InventoryService.MainVirtualCurrencySlotStart），
        /// 不在 inventory.GetItems() 里，也没有 ItemCore，故走 RegisterGoldListing 旁路。
        /// </summary>
        public const int GoldTemplateId = 2681725;

        /// <summary>
        /// 金币券家族（86JP cash/chn_Trade_Gold/gold_Nm.stk）：itemId → 面额（金币）。
        /// 语义：金币寄售上架一个「面额为 face、数量为 count」的金币券挂牌，
        /// 托管金币 = face × count；一口价 BuyoutPrice 是整单【点券】(céra) 价格。
        /// 面额表来自 stackable.lst 逆向（round56 定案）：
        ///   gold_1m=100 万 … gold_10m=1000 万，gold_20m/30m/40m/50m/100m/150m/200m。
        ///   注：2681762=trade_point、2681917=trade_point_event 是代币券，【不属于】金币家族。
        /// </summary>
        private static readonly Dictionary<int, int> GoldDenominations = new()
        {
            { 2681725, 1_000_000 },   // gold_1m
            { 2681726, 2_000_000 },   // gold_2m
            { 2681727, 3_000_000 },   // gold_3m
            { 2681728, 4_000_000 },   // gold_4m
            { 2681729, 5_000_000 },   // gold_5m
            { 2681730, 6_000_000 },   // gold_6m
            { 2681731, 7_000_000 },   // gold_7m
            { 2681732, 8_000_000 },   // gold_8m
            { 2681733, 9_000_000 },   // gold_9m
            { 2681734, 10_000_000 },  // gold_10m
            { 2681735, 20_000_000 },  // gold_20m
            { 2681736, 30_000_000 },  // gold_30m
            { 2683069, 40_000_000 },  // gold_40m
            { 2683070, 50_000_000 },  // gold_50m
            { 2683071, 100_000_000 }, // gold_100m
            { 2683072, 150_000_000 }, // gold_150m
            { 2683073, 200_000_000 }, // gold_200m
        };

        /// <summary>itemId 是否属于金币券家族（金币寄售）。</summary>
        public static bool IsGoldItem(int itemId) => GoldDenominations.ContainsKey(itemId);

        /// <summary>取出金币券面额（每张券兑多少金币）。</summary>
        public static bool TryGetGoldValue(int itemId, out int faceValue)
            => GoldDenominations.TryGetValue(itemId, out faceValue);

        public AuctionRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        internal string ConnectionString => _connectionString;

        // Online commands carry the original lease and lifecycle fence.
        internal AuctionRegisterResult RegisterListing(
            AuctionActor actor, InventoryListType listType, short slotIndex, int expectedItemId,
            int buyoutPrice, int startingPrice, TimeSpan duration, int listingCount = 1,
            byte[] expectedItemCore = null)
        {
            if (IsCrystalSource(listType, slotIndex, expectedItemId))
                return RegisterCrystalListing(actor, listType, slotIndex, expectedItemId,
                    buyoutPrice, startingPrice, duration, listingCount, expectedItemCore);
            // AccountCargo is currently copied into each character lease, not a
            // shared account-owned inventory. A local source CAS can be defeated by
            // a stale lease's baseline save; do not authorize cross-account escrow.
            if (listType == InventoryListType.AccountCargo)
                return AuctionRegisterResult.Fail(AuctionError.ItemNotTradable);
            if (actor == null || expectedItemId <= 0 || IsGoldItem(expectedItemId)
                || !IsSourceList(listType) || slotIndex < 0 || listingCount <= 0
                || expectedItemCore == null || expectedItemCore.Length != ItemCore.Size
                || !ValidDuration(duration) || !ValidPrices(buyoutPrice, startingPrice, listingCount))
                return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);
            AuctionRegisterResult result = null;
            var error = Execute(actor, "auction-register", (connection, transaction) =>
            {
                var inventory = actor.Lease.Inventory;
                if (listType == InventoryListType.Main
                    && (InventoryService.IsVirtualMainSlot(slotIndex) || InventoryService.IsReservedMainSlot(slotIndex)))
                    return AuctionError.ItemNotTradable;
                var core = inventory.GetItem(listType, slotIndex);
                if (core == null || core.ItemId != expectedItemId || !core.ToBytes().SequenceEqual(expectedItemCore))
                    return AuctionError.ItemNotFound;
                var stackable = InventoryStackRuleService.IsStackable(core);
                if ((!stackable && listingCount != 1) || (stackable && core.Count < listingCount))
                    return AuctionError.InvalidRequest;
                var policy = ValidateAuctionTransfer(inventory, core);
                if (policy != AuctionError.None)
                    return policy;
                // Gold consignment has its own quota.  Ordinary listings must
                // not be blocked merely because the character has reached the
                // dedicated gold-market limit.
                if (CountActiveListings(connection, transaction, actor.CharacterId, ordinaryOnly: true)
                    >= AuctionPolicy.MaxActiveListingsPerCharacter)
                    return AuctionError.ListingLimitReached;
                var fee = AuctionPolicy.CalculateListingFee(Math.Max(buyoutPrice, startingPrice));
                var gold = Gold(inventory);
                if (gold < fee)
                    return AuctionError.InsufficientGold;
                var snapshot = BuildSnapshot(inventory, listType, slotIndex, core, listingCount);
                if (!inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, gold - fee)
                    || !InventoryDeleteService.TryConsumeFromSlot(
                        inventory, listType, slotIndex, expectedItemId, listingCount, out var removed)
                    || !removed.Success)
                    return AuctionError.ServerBusy;
                var id = InsertListing(connection, transaction, actor.CharacterId,
                    LoadCharacterName(connection, transaction, actor.CharacterId),
                    snapshot, buyoutPrice, startingPrice, duration);
                result = new AuctionRegisterResult
                {
                    Success = true, ListingId = id, FeeGold = fee, GoldAfter = gold - fee
                };
                return AuctionError.None;
            });
            return error == AuctionError.None ? result : AuctionRegisterResult.Fail(error);
        }

        internal AuctionRegisterResult RegisterGoldListing(
            AuctionActor actor, int itemId, int count, int buyoutPrice, TimeSpan duration,
            int maxActiveListings = AuctionPolicy.MaxGoldListingsWithoutCoupon)
        {
            if (actor == null || count <= 0 || buyoutPrice <= 0 || !ValidDuration(duration)
                || maxActiveListings <= 0
                || !TryGetGoldValue(itemId, out var face) || !ValidGoldPrice(buyoutPrice))
                return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);
            var escrow = (long)face * count;
            if (escrow > int.MaxValue)
                return AuctionRegisterResult.Fail(AuctionError.GoldLimitExceeded);
            // Gold consignment is always a 24-hour listing. The request duration
            // is retained in the parser for ordinary auction compatibility, but
            // it must not control the dedicated gold market.
            duration = AuctionPolicy.GoldListingDuration;
            AuctionRegisterResult result = null;
            var error = Execute(actor, "auction-gold-register", (connection, transaction) =>
            {
                var inventory = actor.Lease.Inventory;
                var gold = Gold(inventory);
                if (gold < escrow)
                    return AuctionError.InsufficientGold;
                if (CountActiveListings(connection, transaction, actor.CharacterId, goldOnly: true)
                    >= maxActiveListings)
                    return AuctionError.ListingLimitReached;
                var core = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = itemId, Count = count };
                var snapshot = BuildSnapshot(inventory, InventoryListType.Main,
                    InventoryService.MainVirtualCurrencySlotStart, core, count);
                if (!inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, gold - (int)escrow))
                    return AuctionError.ServerBusy;
                var id = InsertListing(connection, transaction, actor.CharacterId,
                    LoadCharacterName(connection, transaction, actor.CharacterId), snapshot, buyoutPrice, 0, duration);
                result = new AuctionRegisterResult
                {
                    Success = true, ListingId = id, FeeGold = 0,
                    GoldAfter = gold - (int)escrow
                };
                return AuctionError.None;
            });
            return error == AuctionError.None ? result : AuctionRegisterResult.Fail(error);
        }

        private AuctionError Execute(
            AuctionActor actor, string operation, Func<SqliteConnection, SqliteTransaction, AuctionError> apply)
        {
            if (actor == null || !actor.IsCurrent()
                || !string.Equals(actor.Lease.Inventory.Database?.ConnectionString, _connectionString,
                    StringComparison.Ordinal))
                return AuctionError.ServerBusy;
            // Persist unrelated accepted dirty state first; a failed command can safely
            // restore this durable baseline through the existing commit coordinator.
            lock (actor.Lease.SyncRoot)
            {
                if (!actor.IsCurrent() || !InventoryPersistenceService.SaveDirty(actor.Lease) || !actor.IsCurrent())
                    return AuctionError.ServerBusy;
                var error = AuctionError.ServerBusy;
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    actor.Lease, operation, (connection, transaction) =>
                    {
                        if (!actor.IsCurrent())
                            return false;
                        error = apply(connection, transaction);
                        if (error != AuctionError.None)
                            return false;
                        if (!actor.IsCurrent())
                        {
                            error = AuctionError.ServerBusy;
                            return false;
                        }
                        return true;
                    });
                return committed ? AuctionError.None
                    : error == AuctionError.None ? AuctionError.ServerBusy : error;
            }
        }

        private static int Gold(InventoryService inventory) =>
            inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static bool IsSourceList(InventoryListType listType) =>
            listType == InventoryListType.Main || listType == InventoryListType.PersonalCargo;

        private static bool ValidDuration(TimeSpan duration) =>
            duration == TimeSpan.FromHours(6) || duration == TimeSpan.FromHours(12)
                || duration == TimeSpan.FromHours(24) || duration == TimeSpan.FromHours(48);

        private static bool ValidPrices(int buyout, int starting, int count) =>
            buyout >= 0 && starting >= 0 && count > 0 && (buyout > 0 || starting > 0)
                && buyout <= AuctionPolicy.MaxPrice && starting <= AuctionPolicy.MaxPrice
                && (buyout == 0 || starting == 0 || starting < buyout)
                && (long)buyout * count <= int.MaxValue && (long)starting * count <= int.MaxValue;

        private static bool ValidGoldPrice(int buyout) =>
            buyout > 0 && buyout <= AuctionPolicy.MaxPrice;

        private static AuctionError ValidateAuctionTransfer(InventoryService inventory, ItemCore core)
        {
            if (core.SortLockFlag != 0 || (core.EquipmentLockId != 0
                && (!inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                    || itemLock == null || itemLock.State != 0)))
                return AuctionError.ItemNotTradable;
            if (core.ItemKind == ItemCore.KindAvatar
                && (!inventory.AvatarDetails.TryGetDetail(core.AvatarUid, out var avatar)
                    || avatar.ExpireDate >= 1000000000))
                return AuctionError.ItemNotTradable;
            if (core.ItemKind == ItemCore.KindCreature
                && (!inventory.CreatureDetails.TryGetDetail(core.CreatureUid, out var creature)
                    || creature.ExpireDate >= 1000000000))
                return AuctionError.ItemNotTradable;
            var metadata = ItemMetadataResolver.Resolve(core.ItemId);
            // A public listing cannot use mail's same-account binding exception.
            var attach = (metadata?.AttachType ?? string.Empty).Replace(((char)96).ToString(), string.Empty)
                .Trim().ToLowerInvariant();
            if (attach.Contains("account"))
                return AuctionError.ItemNotTradable;
            var policy = MailboxSendPolicy.ValidateAttachment(
                new MailboxSendRequest { SenderCharacterId = inventory.CharacterId,
                    SenderAccountId = inventory.AccountId, ReceiverAccountId = -1 }, core);
            return policy == MailboxSendError.None ? AuctionError.None : MapPolicyError(policy);
        }

        private static int CountActiveListings(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            bool goldOnly = false,
            bool ordinaryOnly = false)
        {
            if (goldOnly && ordinaryOnly)
                throw new ArgumentException("Only one listing category may be selected.", nameof(ordinaryOnly));
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var filter = string.Empty;
                if (goldOnly || ordinaryOnly)
                {
                    var placeholders = string.Join(",", GoldDenominations.Keys.Select((_, index) => "@gold" + index));
                    filter = goldOnly
                        ? " AND item_template_id IN (" + placeholders + ")"
                        : " AND item_template_id NOT IN (" + placeholders + ")";
                }
                command.CommandText = "SELECT COUNT(*) FROM auction_listings " +
                    "WHERE seller_character_id=@cid AND status=0" + filter;
                command.Parameters.AddWithValue("@cid", characterId);
                if (goldOnly || ordinaryOnly)
                {
                    int index = 0;
                    foreach (var itemId in GoldDenominations.Keys)
                        command.Parameters.AddWithValue("@gold" + index++, itemId);
                }
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        public IReadOnlyList<AuctionListing> SearchActiveListings(
            int itemId,
            int limit,
            int offset,
            int excludeSellerCharacterId = 0,
            int filterItemKind = -1,
            string filterGroup = null,
            bool filterGroupPrefix = false,
            string filterUsableJob = null,
            string filterEquipmentType = null,
            string filterExpertType = null,
            string filterStackableCategory = null)
            => SearchActiveListings(itemId > 0 ? new[] { itemId } : Array.Empty<int>(),
                limit, offset, excludeSellerCharacterId, filterItemKind, filterGroup,
                filterGroupPrefix, filterUsableJob, filterEquipmentType, filterExpertType,
                filterStackableCategory);

        public IReadOnlyList<AuctionListing> SearchActiveListings(
            IReadOnlyCollection<int> itemIds,
            int limit,
            int offset,
            int excludeSellerCharacterId = 0,
            int filterItemKind = -1,
            string filterGroup = null,
            bool filterGroupPrefix = false,
            string filterUsableJob = null,
            string filterEquipmentType = null,
            string filterExpertType = null,
            string filterStackableCategory = null)
        {
            var candidates = NormalizeSearchItemIds(itemIds);
            const int maxLimit = 64;
            if (limit <= 0 || limit > maxLimit)
                limit = 24;
            if (offset < 0)
                offset = 0;

            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    // Preserve the source server's visibility policy: search excludes
                    // the current seller; transaction ownership checks remain separate.
                    // excludeSellerCharacterId > 0 时附加 AND seller_character_id != @excludeCid 过滤。
                    var excludeClause = excludeSellerCharacterId > 0
                        ? " AND seller_character_id != @excludeCid"
                        : string.Empty;

                    // 分类过滤：filterItemKind >= 0 时按 item_kind（数字字符串）过滤，-1 不过滤。
                    var kindClause = filterItemKind >= 0
                        ? " AND item_kind = @filterKind"
                        : string.Empty;

                    // ★ round89：普通拍卖行搜索必须排除金币券家族（gold_1m~gold_200m）。
                    //   金币券 kind=3(KindMaterial)，会泄漏进「材料 0x32CA」等普通分类；
                    //   客户端在普通拍卖行列表里把它们当普通道具，购买走 0x014E 普通一口价
                    //   路径（门 row[0x400] 物品对象指针从未被赋值 → 静默 return 不发包），
                    //   表现为 50m/100m 等面额「购买无响应」。金币寄售走专门的
                    //   SearchActiveGoldListings（0x9CA5 / 0x00BA itemId），购买走 0x00B9
                    //   金币路径（门 [0x4d4] 正常），故此处排除不影响金币寄售功能。
                    //   排除区间对齐客户端范围判断 0x145a5a1：2681725-2681736 / 2683069-2683073。
                    const string goldExcludeClause =
                        " AND item_template_id NOT BETWEEN 2681725 AND 2681736" +
                        " AND item_template_id NOT BETWEEN 2683069 AND 2683073";

                    // 装备细分过滤（filterGroup != null 或 filterUsableJob != null 或 filterEquipmentType != null
                    // 或 filterExpertType != null）依赖 PVF 字段，这些字段不在 auction_listings 表中，
                    // 需查出候选后按 item_template_id 反查内存过滤。因此这些过滤维度都不走 SQL 分页
                    // （查全部 active，内存过滤后再截断），避免「先 LIMIT 64 再过滤」导致结果不足一页/缺失。
                    var pageClause = (filterGroup != null || filterUsableJob != null
                        || filterEquipmentType != null || filterExpertType != null || filterStackableCategory != null)
                        ? string.Empty
                        : " LIMIT @limit OFFSET @offset";

                    var itemClause = BindSearchItemIds(command, candidates);
                    command.CommandText = candidates.Length > 0
                        ? $@"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                           FROM auction_listings
                           WHERE status = 0{itemClause}{excludeClause}{kindClause}{goldExcludeClause}
                           ORDER BY buyout_price ASC, listed_at_unix ASC, listing_id ASC{pageClause}"
                        : $@"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                           FROM auction_listings
                           WHERE status = 0{excludeClause}{kindClause}{goldExcludeClause}
                           ORDER BY listed_at_unix DESC, listing_id DESC{pageClause}";
                    if (excludeSellerCharacterId > 0)
                        command.Parameters.AddWithValue("@excludeCid", excludeSellerCharacterId);
                    if (filterItemKind >= 0)
                        command.Parameters.AddWithValue("@filterKind", filterItemKind.ToString());
                    if (filterGroup == null && filterUsableJob == null
                        && filterEquipmentType == null && filterExpertType == null && filterStackableCategory == null)
                    {
                        command.Parameters.AddWithValue("@limit", limit);
                        command.Parameters.AddWithValue("@offset", offset);
                    }
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }

            // Apply all typed resource filters before one final pagination step.
            IEnumerable<AuctionListing> filtered = results;
            if (filterGroup != null)
                filtered = filtered.Where(row => MatchesResourceGroup(
                    row.Item.ItemTemplateId, filterGroup, filterGroupPrefix));
            if (filterUsableJob != null)
                filtered = filtered.Where(row =>
                {
                    var job = EquipmentJob(row.Item.ItemTemplateId);
                    return !string.IsNullOrWhiteSpace(job) && (filterUsableJob == "[all]"
                        || JobTagMatches(job, filterUsableJob) || JobTagMatches(job, "[all]"));
                });
            if (filterEquipmentType != null)
                filtered = filtered.Where(row =>
                    filterEquipmentType == CreatureEggFilter
                        ? IsCreatureEgg(row.Item.ItemTemplateId)
                        : string.Equals(ItemMetadataResolver.ResolveEquipmentType(row.Item.ItemTemplateId),
                            filterEquipmentType, StringComparison.OrdinalIgnoreCase));
            if (filterExpertType != null)
                filtered = filtered.Where(row => string.Equals(
                    NormalizeCategory(StackableItemProvider.Load(row.Item.ItemTemplateId)?.ExpertType),
                    NormalizeCategory(filterExpertType), StringComparison.OrdinalIgnoreCase));
            if (filterStackableCategory != null)
                filtered = filtered.Where(row => MatchesStackableCategory(
                    ItemMetadataResolver.Resolve(row.Item.ItemTemplateId)?.StackableType, filterStackableCategory));
            return (filterGroup != null || filterUsableJob != null || filterEquipmentType != null
                || filterExpertType != null || filterStackableCategory != null)
                ? filtered.Skip(offset).Take(limit).ToList() : results;
        }

        internal const string CreatureEggFilter = "\u0001__creature_egg__";
        internal const string StackableParentFilter = "\u0001__auction_stackables__";
        internal const string AccessoryParentFilter = "\u0001__auction_accessories__";
        internal const string ExpertParentFilter = "\u0001__auction_expert_products__";

        private static int[] NormalizeSearchItemIds(IReadOnlyCollection<int> itemIds)
        {
            ArgumentNullException.ThrowIfNull(itemIds);
            if (itemIds.Count > byte.MaxValue || itemIds.Any(id => id <= 0))
                throw new ArgumentOutOfRangeException(nameof(itemIds));
            return itemIds.Distinct().OrderBy(id => id).ToArray();
        }

        private static string BindSearchItemIds(SqliteCommand command, IReadOnlyList<int> itemIds)
        {
            if (itemIds.Count == 0) return string.Empty;
            var names = new string[itemIds.Count];
            for (int i = 0; i < itemIds.Count; i++)
            {
                names[i] = "@item" + i;
                command.Parameters.AddWithValue(names[i], itemIds[i]);
            }
            return " AND item_template_id IN (" + string.Join(",", names) + ")";
        }

        private static bool MatchesResourceGroup(int itemId, string filter, bool prefix)
        {
            if (filter == StackableParentFilter)
            {
                return ItemMetadataResolver.TryResolveItemKind(itemId, out byte kind)
                    && kind is ItemCore.KindConsumable or ItemCore.KindMaterial or ItemCore.KindQuest
                        or ItemCore.KindSpecialMaterial or ItemCore.KindExpertJobMaterial;
            }
            if (filter == AccessoryParentFilter)
            {
                return MatchesGroupValues(EquipmentGroup(itemId), "amulet|ring|wrist", false)
                    || string.Equals(NormalizeCategory(ItemMetadataResolver.ResolveEquipmentType(itemId)),
                        "title name", StringComparison.OrdinalIgnoreCase);
            }
            if (filter == ExpertParentFilter)
            {
                var stackable = StackableItemProvider.Load(itemId);
                return stackable != null && (MatchesGroupValues(NormalizeCategory(stackable.ExpertType),
                    "alchemist|doll_controller|enchanter", false)
                    || string.Equals(NormalizeCategory(stackable.StackableType),
                        "material expert job", StringComparison.OrdinalIgnoreCase));
            }
            return MatchesGroupValues(EquipmentGroup(itemId), filter, prefix);
        }

        private static bool MatchesGroupValues(string value, string filter, bool prefix)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return filter.Split('|').Any(candidate => prefix
                ? string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase)
                : string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private static string EquipmentGroup(int id) =>
            ItemMetadataResolver.TryLoadEquipmentFile(id, out var equipment) ? equipment.ItemGroupName : null;

        private static string EquipmentJob(int id) =>
            ItemMetadataResolver.TryLoadEquipmentFile(id, out var equipment) ? equipment.UsableJob : null;

        private static bool IsCreatureEgg(int id) =>
            ItemMetadataResolver.TryLoadEquipmentFile(id, out var equipment)
                && NormalizeCategory(equipment.EquipmentType) == "creature" && equipment.SubType == 1;

        private static string NormalizeCategory(string value)
        {
            var normalized = (value ?? string.Empty).Replace(((char)96).ToString(), string.Empty).Trim();
            // The PVF library retains a tag's auxiliary numeric data. Match its
            // primary bracket label, as ItemMetadataResolver does for ItemCore kind.
            if (normalized.StartsWith("[", StringComparison.Ordinal))
            {
                int end = normalized.IndexOf(']', 1);
                if (end > 1) return normalized.Substring(1, end - 1).Trim();
            }
            return normalized.Trim('[', ']', ' ');
        }

        internal static bool MatchesStackableCategory(string stackableType, string category)
        {
            var type = NormalizeCategory(stackableType).ToLowerInvariant();
            return NormalizeCategory(category).ToLowerInvariant() switch
            {
                "consumable" => !string.IsNullOrWhiteSpace(type)
                    && !MatchesStackableCategory(type, "throw") && !MatchesStackableCategory(type, "gacha")
                    && !MatchesStackableCategory(type, "misc"),
                "throw" => type is "throw" or "set",
                "gacha" => type is "booster selection" or "booster random" or "random reward item"
                    or "random upgradable legacy" or "random ss upgradable legacy" or "random goblin upgradable legacy"
                    or "random disguise" or "multi upgradable legacy" or "multi upgradable legacy bonus cera" or "quiz item",
                "misc" => type is "etc" or "unlimited etc" or "dye" or "contract" or "disguise" or "coupon"
                    or "couple room item" or "avatar gift" or "creature expitem" or "stackable legacy"
                    or "only effect" or "scale effect",
                _ => false
            };
        }

        /// <summary>
        /// 金币寄售搜索（分类 ID 0x9CA5 = "其他"，金币寄售面板根，含 17 个面额子节点）。
        ///   itemId > 0 且属于金币券家族：精确搜索该面额的 Active 挂牌。
        ///   itemId == 0：浏览全部 17 个面额（gold_1m~gold_200m）的 Active 挂牌（按面额/单价/上架时间排序）。
        /// 金币券无职业/装备/副职业维度，识别靠 itemId（IsGoldItem）+ 分类 ID 0x9CA5，
        /// 故不走分类维度过滤（参考 HandleSearch 中 isGoldBrowse 旁路）。
        /// </summary>
        public IReadOnlyList<AuctionListing> SearchActiveGoldListings(
            int itemId,
            int limit,
            int offset,
            int excludeSellerCharacterId = 0)
            => SearchActiveGoldListings(itemId > 0 ? new[] { itemId } : Array.Empty<int>(),
                limit, offset, excludeSellerCharacterId);

        public IReadOnlyList<AuctionListing> SearchActiveGoldListings(
            IReadOnlyCollection<int> itemIds,
            int limit,
            int offset,
            int excludeSellerCharacterId = 0)
        {
            var requested = NormalizeSearchItemIds(itemIds);
            var candidates = requested.Length == 0 ? GoldDenominations.Keys.OrderBy(id => id).ToArray()
                : requested.Where(IsGoldItem).ToArray();
            // A supplied non-gold key must not become an unfiltered gold browse.
            if (candidates.Length == 0) return Array.Empty<AuctionListing>();
            const int maxLimit = 64;
            if (limit <= 0 || limit > maxLimit)
                limit = 24;
            if (offset < 0)
                offset = 0;

            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    // Preserve the current seller visibility policy.
                    var excludeClause = excludeSellerCharacterId > 0
                        ? " AND seller_character_id != @excludeCid"
                        : string.Empty;

                    var itemFilter = BindSearchItemIds(command, candidates);

                    // 金币寄售挂单禁止竞价，starting_price / current_bid 必为 0；按面额升序、单价升序、上架时间升序排列。
                    command.CommandText = $@"SELECT listing_id, seller_character_id, seller_name, item_type,
                                              source_list_type, source_slot_index, item_template_id, item_kind,
                                              item_count, instance_value, durability, seal_flag, option_value,
                                              expire_time, marker16, pet_serial_or_handle, extra_json,
                                              item_core_data, detail_json, buyout_price, status,
                                              buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                              sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                                       FROM auction_listings
                                       WHERE status = 0{itemFilter}{excludeClause}
                                       ORDER BY item_template_id ASC, buyout_price ASC, listed_at_unix ASC, listing_id ASC
                                       LIMIT @limit OFFSET @offset";
                    if (excludeSellerCharacterId > 0)
                        command.Parameters.AddWithValue("@excludeCid", excludeSellerCharacterId);
                    command.Parameters.AddWithValue("@limit", limit);
                    command.Parameters.AddWithValue("@offset", offset);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// 判断装备的 [usable job] 字符串是否包含目标职业标签（如 "[fighter]"）。
        /// [usable job] 可能是多值（如 "`[fighter]` `[at fighter]`"），StripBacktick 后
        /// 残留 "`[fighter]` `[at fighter]`" 之类格式，精确 Equals 会漏掉男/女通用装备。
        /// 这里用正则提取所有 [xxx] 标签逐一比对，天然兼容单值/多值/残留反引号。
        /// </summary>
        private static bool JobTagMatches(string usableJob, string targetJob)
        {
            if (string.IsNullOrEmpty(usableJob) || string.IsNullOrEmpty(targetJob))
                return false;
            var matches = System.Text.RegularExpressions.Regex.Matches(
                usableJob, @"\[[^\]]+\]");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (string.Equals(m.Value, targetJob, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public IReadOnlyList<AuctionListing> LoadListingsBySeller(
            int sellerCharacterId,
            AuctionListingStatus status)
        {
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                           FROM auction_listings
                           WHERE seller_character_id = @cid AND status = @status
                           ORDER BY listed_at_unix DESC LIMIT 64";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    command.Parameters.AddWithValue("@status", (int)status);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// round58：加载某卖家「所有状态」的历史挂牌，供「我的拍卖历史」0x00BE 展示。
        /// 与 LoadListingsBySeller（仅 Active）区别在于不限定 status —— 一并展示 Active / Sold /
        /// Cancelled / Expired / Settled。模型上沿用 AuctionListing（status 字段区分语义）。
        /// </summary>
        public IReadOnlyList<AuctionListing> LoadAllListingsBySeller(int sellerCharacterId)
        {
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                           FROM auction_listings
                           WHERE seller_character_id = @cid
                           ORDER BY listed_at_unix DESC LIMIT 128";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }
            return results;
        }

        public AuctionListing GetListing(long listingId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                           FROM auction_listings WHERE listing_id = @id";
                    command.Parameters.AddWithValue("@id", listingId);
                    using (var reader = command.ExecuteReader())
                        return reader.Read() ? ReadListing(reader) : null;
                }
            }
        }

        internal WalletSnapshot ReadWallet(AuctionActor actor)
        {
            if (actor == null || !actor.IsCurrent())
                return null;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var wallet = CurrencyService.LoadWallet(connection, null, actor.CharacterId);
                return actor.IsCurrent() ? wallet : null;
            }
        }

        internal AuctionBuyoutResult Buyout(long listingId, AuctionActor actor)
        {
            if (listingId <= 0 || actor == null)
                return AuctionBuyoutResult.Fail(AuctionError.InvalidRequest);
            AuctionBuyoutResult result = null;
            var error = Execute(actor, "auction-buyout", (connection, transaction) =>
            {
                ReconcileExpired(connection, transaction, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var listing = GetListing(connection, transaction, listingId);
                if (listing == null)
                    return AuctionError.ListingNotFound;
                if (listing.Status != AuctionListingStatus.Active)
                    return AuctionError.ListingNotActive;
                if (listing.SellerCharacterId == actor.CharacterId)
                    return AuctionError.OwnListing;
                bool goldListing = IsGoldItem(listing.Item.ItemTemplateId);
                var total = goldListing
                    ? listing.BuyoutPrice
                    : (long)listing.BuyoutPrice * listing.Item.ItemCount;
                if (listing.BuyoutPrice <= 0 || listing.Item.ItemCount <= 0 || total > int.MaxValue)
                    return AuctionError.PriceOutOfRange;
                var gold = Gold(actor.Lease.Inventory);
                if (goldListing)
                {
                    if (!TryGetGoldValue(listing.Item.ItemTemplateId, out var face)
                        || (long)face * listing.Item.ItemCount > int.MaxValue)
                        return AuctionError.GoldLimitExceeded;
                    // The purchased gold and seller proceeds remain claimable
                    // mail rewards. Carry/balance limits are checked at claim,
                    // rather than crediting either wallet during this sale.
                    if (!CurrencyService.TrySpendCera(connection, transaction, actor.CharacterId, (int)total))
                        return AuctionError.InsufficientGold;
                }
                else
                {
                    if (gold < total)
                        return AuctionError.InsufficientGold;
                    if (!actor.Lease.Inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart, gold - (int)total))
                        return AuctionError.ServerBusy;
                }
                if (!Sell(connection, transaction, listing, actor.CharacterId,
                    LoadCharacterName(connection, transaction, actor.CharacterId), (int)total))
                    return AuctionError.ListingNotActive;
                // A buyout is a new payment, even by the current leading bidder.
                // Every old escrow is refunded by its unique persisted bid ID.
                MarkLeadingBidsForRefund(connection, transaction, listingId);
                result = new AuctionBuyoutResult
                {
                    Success = true, ListingId = listingId, IsGold = goldListing,
                    GoldAfter = goldListing ? Gold(actor.Lease.Inventory) : gold - (int)total
                };
                return AuctionError.None;
            });
            return error == AuctionError.None ? result : AuctionBuyoutResult.Fail(error);
        }

        internal AuctionBidResult PlaceBid(long listingId, AuctionActor actor, int bidAmount)
        {
            if (listingId <= 0 || actor == null || bidAmount <= 0)
                return AuctionBidResult.Fail(AuctionError.InvalidRequest);
            AuctionBidResult result = null;
            var error = Execute(actor, "auction-bid", (connection, transaction) =>
            {
                ReconcileExpired(connection, transaction, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var listing = GetListing(connection, transaction, listingId);
                if (listing == null)
                    return AuctionError.ListingNotFound;
                if (listing.Status != AuctionListingStatus.Active)
                    return AuctionError.ListingNotActive;
                if (listing.SellerCharacterId == actor.CharacterId)
                    return AuctionError.OwnListing;
                if (IsGoldItem(listing.Item.ItemTemplateId))
                    return AuctionError.BidTooLow;
                var startingTotal = (long)listing.StartingPrice * listing.Item.ItemCount;
                var buyoutTotal = (long)listing.BuyoutPrice * listing.Item.ItemCount;
                if (listing.Item.ItemCount <= 0 || startingTotal > int.MaxValue || buyoutTotal > int.MaxValue)
                    return AuctionError.PriceOutOfRange;
                if (bidAmount <= listing.CurrentBid || (listing.CurrentBid == 0 && bidAmount < startingTotal)
                    || (listing.BuyoutPrice > 0 && bidAmount >= buyoutTotal))
                    return AuctionError.BidTooLow;
                var inventory = actor.Lease.Inventory;
                var gold = Gold(inventory);
                if (gold < bidAmount)
                    return AuctionError.InsufficientGold;
                // Locate the previous escrow BEFORE inserting the next bid. This
                // matters when the highest bidder raises their own bid.
                var oldBid = listing.CurrentBidderId > 0
                    ? FindCurrentBidId(connection, transaction, listingId, listing.CurrentBidderId) : 0;
                if (listing.CurrentBid > 0 && oldBid <= 0)
                    return AuctionError.ServerBusy;
                MarkLeadingBidsForRefund(connection, transaction, listingId);
                if (!inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, gold - bidAmount))
                    return AuctionError.ServerBusy;
                var name = LoadCharacterName(connection, transaction, actor.CharacterId);
                InsertBid(connection, transaction, listingId, actor.CharacterId, name, bidAmount);
                UpdateListingBid(connection, transaction, listingId, bidAmount, actor.CharacterId, name);
                result = new AuctionBidResult
                {
                    Success = true, ListingId = listingId, BidAmount = bidAmount, GoldAfter = gold - bidAmount,
                    OutbidCharacterId = listing.CurrentBidderId, OutbidAmount = listing.CurrentBid, OutbidBidId = oldBid
                };
                return AuctionError.None;
            });
            return error == AuctionError.None ? result : AuctionBidResult.Fail(error);
        }

        private static bool Sell(
            SqliteConnection connection, SqliteTransaction transaction, AuctionListing listing,
            int buyerId, string buyerName, int dealPrice)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
SET status=1,buyer_character_id=@buyer,buyer_name=@name,sold_at_unix=@now,actual_deal_price=@price,
    extra_json=@extra
WHERE listing_id=@id AND status=0";
                command.Parameters.AddWithValue("@buyer", buyerId);
                command.Parameters.AddWithValue("@name", buyerName);
                command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@price", dealPrice);
                command.Parameters.AddWithValue("@extra", IsGoldItem(listing.Item.ItemTemplateId)
                    ? AuctionGoldDelivery.MarkMail(listing.Item.ExtraJson) : listing.Item.ExtraJson ?? "{}");
                command.Parameters.AddWithValue("@id", listing.ListingId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static void MarkLeadingBidsForRefund(
            SqliteConnection connection, SqliteTransaction transaction, long listingId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE auction_bids SET status=1 WHERE listing_id=@id AND status=0";
                command.Parameters.AddWithValue("@id", listingId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>加载某角色的出价记录（round57 放宽：领先中 status=0 + 被超价 status=1）。</summary>
        /// <remarks>
        ///   round56 版 WHERE status=0 会让所有 listing 已 Settled 的领先 bid 也被丢掉——
        ///   实测发现 settlement 流程未必把 bid.status 从 0 改到 2（中标），导致用户在
        ///   「我的竞价」面板看不到任何历史。现在返回该角色最近 64 条 bid，由 Handler
        ///   内存过滤 bid.Status &lt;= 1（领先中 + 被超价）。同 listing 多条 bid 由 Handler
        ///   按 listing_id dedup 后只取最新一笔。
        /// </remarks>
        public IReadOnlyList<AuctionBidRecord> LoadMyActiveBids(int bidderCharacterId)
        {
            var results = new List<AuctionBidRecord>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id, listing_id, bidder_character_id, bid_amount, bid_at_unix, status
                           FROM auction_bids
                           WHERE bidder_character_id = @cid
                           ORDER BY bid_at_unix DESC, bid_id DESC LIMIT 64";
                    command.Parameters.AddWithValue("@cid", bidderCharacterId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadBidRecord(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>加载某挂牌的全部出价记录（状态不限），供结算/退回遍历。</summary>
        public IReadOnlyList<AuctionBidRecord> LoadBidsByListing(long listingId)
        {
            var results = new List<AuctionBidRecord>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id, listing_id, bidder_character_id, bid_amount, bid_at_unix, status
                           FROM auction_bids
                           WHERE listing_id = @id
                           ORDER BY bid_at_unix ASC, bid_id ASC";
                    command.Parameters.AddWithValue("@id", listingId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadBidRecord(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>取走所有「被超价但金币尚未退回」的出价（status=1, refund_mail_sent=0），供结算退回。</summary>
        public IReadOnlyList<AuctionBidRecord> LoadPendingOutbidRefunds(int limit)
        {
            var results = new List<AuctionBidRecord>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id, listing_id, bidder_character_id, bid_amount, bid_at_unix, status
                           FROM auction_bids
                           WHERE status = 1 AND refund_mail_sent = 0
                           ORDER BY bid_at_unix ASC, bid_id ASC LIMIT @limit";
                    command.Parameters.AddWithValue("@limit", limit);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadBidRecord(reader));
                    }
                }
            }
            return results;
        }

        internal AuctionBidRecord GetPendingRefund(long bidId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id,listing_id,bidder_character_id,bid_amount,bid_at_unix,status
FROM auction_bids WHERE bid_id=@id AND status=1 AND refund_mail_sent=0";
                    command.Parameters.AddWithValue("@id", bidId);
                    using (var reader = command.ExecuteReader())
                        return reader.Read() ? ReadBidRecord(reader) : null;
                }
            }
        }

        private static long FindCurrentBidId(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidderCharacterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT bid_id FROM auction_bids
                    WHERE listing_id = @id AND bidder_character_id = @cid AND status = 0
                    ORDER BY bid_id DESC LIMIT 1";
                command.Parameters.AddWithValue("@id", listingId);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                var result = command.ExecuteScalar();
                return result is long id ? id : 0L;
            }
        }

        /// <summary>标记某出价金币已退回（refund_mail_sent=1），避免重复退回。</summary>
        internal void MarkBidRefunded(long bidId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_bids SET refund_mail_sent = 1 WHERE bid_id = @id AND status = 1";
                    command.Parameters.AddWithValue("@id", bidId);
                    command.ExecuteNonQuery();
                }
            }
        }


        private static AuctionBidRecord ReadBidRecord(SqliteDataReader reader)
        {
            return new AuctionBidRecord
            {
                BidId = reader.GetInt64(0),
                ListingId = reader.GetInt64(1),
                BidderCharacterId = reader.GetInt32(2),
                BidAmount = reader.GetInt32(3),
                BidAtUnix = reader.GetInt64(4),
                Status = reader.GetInt32(5),
            };
        }

        private static void InsertBid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidderCharacterId,
            string bidderName,
            int bidAmount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO auction_bids (
                    listing_id, bidder_character_id, bidder_name, bid_amount, status, bid_at_unix)
                VALUES (@listingId, @cid, @name, @amount, 0, @at)";
                command.Parameters.AddWithValue("@listingId", listingId);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                command.Parameters.AddWithValue("@name", bidderName ?? string.Empty);
                command.Parameters.AddWithValue("@amount", bidAmount);
                command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
        }

        private static void UpdateListingBid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidAmount,
            int bidderCharacterId,
            string bidderName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
                    SET current_bid = @bid,
                        current_bidder_id = @cid,
                        current_bidder_name = @name,
                        bid_count = bid_count + 1,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE listing_id = @id";
                command.Parameters.AddWithValue("@bid", bidAmount);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                command.Parameters.AddWithValue("@name", bidderName ?? string.Empty);
                command.Parameters.AddWithValue("@id", listingId);
                command.ExecuteNonQuery();
            }
        }

        internal AuctionCancelResult Cancel(long listingId, AuctionActor actor)
        {
            if (listingId <= 0 || actor == null)
                return AuctionCancelResult.Fail(AuctionError.InvalidRequest);
            AuctionCancelResult result = null;
            var error = Execute(actor, "auction-cancel", (connection, transaction) =>
            {
                ReconcileExpired(connection, transaction, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var listing = GetListing(connection, transaction, listingId);
                if (listing == null || listing.SellerCharacterId != actor.CharacterId)
                    return AuctionError.ListingNotFound;
                if (listing.Status != AuctionListingStatus.Active)
                    return AuctionError.ListingNotActive;
                var goldListing = IsGoldItem(listing.Item.ItemTemplateId);
                if (goldListing
                    && (!TryGetGoldValue(listing.Item.ItemTemplateId, out _)
                        || listing.Item.ItemCount <= 0))
                    return AuctionError.InvalidRequest;
                if (!TransitionStatus(connection, transaction, listingId, AuctionListingStatus.Cancelled, 0, string.Empty))
                    return AuctionError.ListingNotActive;
                MarkLeadingBidsForRefund(connection, transaction, listingId);
                result = new AuctionCancelResult { Success = true, ListingId = listingId, IsGold = goldListing };
                return AuctionError.None;
            });
            return error == AuctionError.None ? result : AuctionCancelResult.Fail(error);
        }

        // Both expiration outcomes and the winning bid are frozen together. A reader
        // can never observe an expired winning auction as a seller-return candidate.
        public int MarkExpiredListings(long nowUnix)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var affected = ReconcileExpired(connection, transaction, nowUnix);
                    transaction.Commit();
                    return affected;
                }
            }
        }

        private static int ReconcileExpired(SqliteConnection connection, SqliteTransaction transaction, long nowUnix)
        {
            int affected;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
SET status=CASE WHEN current_bid>0 AND current_bidder_id>0 THEN 1 ELSE 3 END,
    buyer_character_id=CASE WHEN current_bid>0 AND current_bidder_id>0 THEN current_bidder_id ELSE 0 END,
    buyer_name=CASE WHEN current_bid>0 AND current_bidder_id>0 THEN current_bidder_name ELSE '' END,
    sold_at_unix=CASE WHEN current_bid>0 AND current_bidder_id>0 THEN @now ELSE 0 END,
    actual_deal_price=CASE WHEN current_bid>0 AND current_bidder_id>0 THEN current_bid ELSE 0 END
WHERE status=0 AND expires_at_unix<=@now";
                command.Parameters.AddWithValue("@now", nowUnix);
                affected = command.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_bids SET status=2
WHERE status=0 AND EXISTS (
    SELECT 1 FROM auction_listings l
    WHERE l.listing_id=auction_bids.listing_id AND l.status=1
      AND l.buyer_character_id=auction_bids.bidder_character_id
      AND l.actual_deal_price=auction_bids.bid_amount)";
                command.ExecuteNonQuery();
            }
            return affected;
        }

        public IReadOnlyList<AuctionListing> LoadPendingSettlements(int limit)
        {
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price,
                                  actual_deal_price
                           FROM auction_listings
                           WHERE status IN (1,2,3) AND settle_mail_count<2
                           ORDER BY listed_at_unix ASC LIMIT @limit";
                    command.Parameters.AddWithValue("@limit", Math.Max(1, Math.Min(200, limit)));
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }
            return results;
        }

        // Mailbox's unique idempotency key commits the mail first. This durable stage
        // is monotonic; a failed second mail does not close the Sold listing.
        internal void MarkMailStage(long listingId, int mailCount, bool complete)
        {
            if (listingId <= 0 || mailCount < 1 || mailCount > 2)
                throw new ArgumentOutOfRangeException(nameof(mailCount));
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_listings
SET settle_mail_count=MAX(settle_mail_count,@count),
    status=CASE WHEN @complete=1 THEN 4 ELSE status END
WHERE listing_id=@id AND status IN (1,2,3) AND settle_mail_count<=@count";
                    command.Parameters.AddWithValue("@count", mailCount);
                    command.Parameters.AddWithValue("@complete", complete ? 1 : 0);
                    command.Parameters.AddWithValue("@id", listingId);
                    command.ExecuteNonQuery();
                }
            }
        }

        public int CountActiveListings(int sellerCharacterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*) FROM auction_listings
                        WHERE seller_character_id = @cid AND status = @active";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                    var result = command.ExecuteScalar();
                    return result is long value ? (int)value : 0;
                }
            }
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------

        internal static AuctionItemSnapshot BuildSnapshot(
            InventoryService inventory, InventoryListType listType, short slotIndex, ItemCore source, int listedCount)
        {
            var core = source.Copy();
            if (InventoryStackRuleService.IsStackable(core))
                core.Count = listedCount;
            core.SortLockFlag = 0;
            core.EquipmentLockId = 0;
            return new AuctionItemSnapshot
            {
                ItemType = core.ItemKind == ItemCore.KindAvatar ? (byte)1
                    : core.ItemKind is ItemCore.KindCreature or ItemCore.KindCreatureEquipment or ItemCore.KindCreatureConsumable
                        ? (byte)3 : (byte)0,
                SourceListType = (int)listType, SourceSlotIndex = slotIndex,
                ItemTemplateId = core.ItemId, ItemKind = core.ItemKind.ToString(),
                ItemCount = listedCount, InstanceValue = core.Value,
                Durability = core.Durability, SealFlag = core.SealFlag, OptionValue = core.AbilityNo,
                ExpireTime = core.ExpireTime, Marker16 = core.Marker16, PetSerialOrHandle = core.CreatureUid,
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = MailboxItemDetailCodec.Capture(inventory, source)
            };
        }

        private static string LoadCharacterName(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT name FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                var result = command.ExecuteScalar();
                // characters.name 列存的是 GBK 字节（BLOB），旧代码 `result as string`
                // 对 BLOB 恒为 null，导致 seller_name 空串。这里按字节解码。
                if (result is byte[] bytes)
                    return ClientTextEncoding.GetString(bytes);
                if (result is string s)
                    return s;
                return string.Empty;
            }
        }

        private static long InsertListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int sellerCharacterId,
            string sellerName,
            AuctionItemSnapshot snapshot,
            int buyoutPrice,
            int startingPrice,
            TimeSpan duration)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expires = now + (long)duration.TotalSeconds;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO auction_listings (
                    seller_character_id, seller_name,
                    item_type, source_list_type, source_slot_index,
                    item_template_id, item_kind, item_count, instance_value,
                    durability, seal_flag, option_value, expire_time, marker16,
                    pet_serial_or_handle, extra_json, item_core_data, detail_json,
                    buyout_price, starting_price, status, listed_at_unix, expires_at_unix)
                VALUES (
                    @cid, @name,
                    @itemType, @srcList, @srcSlot,
                    @itemId, @itemKind, @itemCount, @instanceValue,
                    @durability, @sealFlag, @optionValue, @expireTime, @marker16,
                    @petHandle, @extraJson, @coreData, @detailJson,
                    @price, @startingPrice, 0, @listedAt, @expiresAt);
                SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@cid", sellerCharacterId);
                command.Parameters.AddWithValue("@name", sellerName ?? string.Empty);
                command.Parameters.AddWithValue("@itemType", snapshot.ItemType);
                command.Parameters.AddWithValue("@srcList", snapshot.SourceListType);
                command.Parameters.AddWithValue("@srcSlot", snapshot.SourceSlotIndex);
                command.Parameters.AddWithValue("@itemId", snapshot.ItemTemplateId);
                command.Parameters.AddWithValue("@itemKind", snapshot.ItemKind ?? "unknown");
                command.Parameters.AddWithValue("@itemCount", snapshot.ItemCount);
                command.Parameters.AddWithValue("@instanceValue", snapshot.InstanceValue);
                command.Parameters.AddWithValue("@durability", snapshot.Durability);
                command.Parameters.AddWithValue("@sealFlag", snapshot.SealFlag);
                command.Parameters.AddWithValue("@optionValue", snapshot.OptionValue);
                command.Parameters.AddWithValue("@expireTime", snapshot.ExpireTime);
                command.Parameters.AddWithValue("@marker16", snapshot.Marker16);
                command.Parameters.AddWithValue("@petHandle", snapshot.PetSerialOrHandle);
                command.Parameters.AddWithValue("@extraJson", snapshot.ExtraJson ?? "{}");
                command.Parameters.AddWithValue("@coreData", snapshot.ItemCoreData ?? Array.Empty<byte>());
                command.Parameters.AddWithValue("@detailJson", snapshot.DetailJson ?? string.Empty);
                command.Parameters.AddWithValue("@price", buyoutPrice);
                command.Parameters.AddWithValue("@startingPrice", startingPrice);
                command.Parameters.AddWithValue("@listedAt", now);
                command.Parameters.AddWithValue("@expiresAt", expires);
                var result = command.ExecuteScalar();
                return result is long value ? value : 0;
            }
        }

        private static bool TransitionStatus(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            AuctionListingStatus target,
            int buyerCharacterId,
            string buyerName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
                    SET status = @status,
                        buyer_character_id = @buyerCid,
                        buyer_name = @buyerName,
                        sold_at_unix = @soldAt
                    WHERE listing_id = @id AND status = @active";
                command.Parameters.AddWithValue("@status", (int)target);
                command.Parameters.AddWithValue("@buyerCid", buyerCharacterId);
                command.Parameters.AddWithValue("@buyerName", buyerName ?? string.Empty);
                command.Parameters.AddWithValue("@soldAt",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@id", listingId);
                command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static AuctionListing GetListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price, actual_deal_price
                           FROM auction_listings WHERE listing_id = @id";
                command.Parameters.AddWithValue("@id", listingId);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? ReadListing(reader) : null;
            }
        }

        private static AuctionListing ReadListing(SqliteDataReader reader)
        {
            return new AuctionListing
            {
                ListingId = reader.GetInt64(0),
                SellerCharacterId = reader.GetInt32(1),
                SellerName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Item = new AuctionItemSnapshot
                {
                    ItemType = reader.GetByte(3),
                    SourceListType = reader.GetInt32(4),
                    SourceSlotIndex = reader.GetInt32(5),
                    ItemTemplateId = reader.GetInt32(6),
                    ItemKind = reader.IsDBNull(7) ? "unknown" : reader.GetString(7),
                    ItemCount = reader.GetInt32(8),
                    InstanceValue = reader.GetInt32(9),
                    Durability = reader.GetInt32(10),
                    SealFlag = reader.GetInt32(11),
                    OptionValue = reader.GetInt32(12),
                    ExpireTime = reader.GetInt32(13),
                    Marker16 = reader.GetInt32(14),
                    PetSerialOrHandle = reader.GetInt32(15),
                    ExtraJson = reader.IsDBNull(16) ? "{}" : reader.GetString(16),
                    ItemCoreData = reader.IsDBNull(17) ? Array.Empty<byte>() : (byte[])reader.GetValue(17),
                    DetailJson = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                },
                BuyoutPrice = reader.GetInt32(19),
                Status = (AuctionListingStatus)reader.GetInt32(20),
                BuyerCharacterId = reader.GetInt32(21),
                BuyerName = reader.IsDBNull(22) ? string.Empty : reader.GetString(22),
                ListedAtUnix = reader.GetInt64(23),
                ExpiresAtUnix = reader.GetInt64(24),
                SoldAtUnix = reader.IsDBNull(25) ? 0 : reader.GetInt64(25),
                SettleMailCount = reader.GetInt32(26),
                // ★ 竞价字段：序号 27~30 紧跟 settle_mail_count（所有 SELECT 已统一追加，
                //   见 SearchActiveListings / LoadListingsBySeller / GetListing /
                //   LoadPendingSettlements 的列清单）。
                CurrentBid = reader.GetInt32(27),
                CurrentBidderId = reader.GetInt32(28),
                CurrentBidderName = reader.IsDBNull(29) ? string.Empty : reader.GetString(29),
                BidCount = reader.GetInt32(30),
                // 起拍价：序号 31（SELECT 列表末尾，紧跟 bid_count）。
                StartingPrice = reader.IsDBNull(31) ? 0 : reader.GetInt32(31),
                ActualDealPrice = reader.GetInt32(32),
            };
        }

        private static AuctionError MapPolicyError(MailboxSendError error)
        {
            switch (error)
            {
                case MailboxSendError.ItemLocked:
                case MailboxSendError.NotTradable:
                case MailboxSendError.AccountBound:
                case MailboxSendError.TradeRestricted:
                    return AuctionError.ItemNotTradable;
                default:
                    return AuctionError.InvalidRequest;
            }
        }
    }

    /// <summary>Extracted server operating profile; these numbers are not asserted as official PVF policy.</summary>
    public static class AuctionPolicy
    {
        public const int MaxActiveListingsPerCharacter = 100;
        public const int MaxGoldListingsWithoutCoupon = 5;
        public const int MaxGoldListingsWithCoupon = 30;
        public static readonly TimeSpan DefaultListingDuration = TimeSpan.FromHours(48);
        public static readonly TimeSpan GoldListingDuration = TimeSpan.FromHours(24);
        public const double ListingFeeRate = 0.01;
        public const double SaleTaxRate = 0.05;
        // Kept as a compatibility symbol for older callers; gold consignment
        // no longer charges a separate fixed listing fee.
        public const int GoldListingFlatFee = 0;
        public const int MaxPrice = 400_000_000;

        public static int CalculateListingFee(int price) => Math.Min(price / 100, price / 10);

        public static int CalculateSellerProceeds(int price) =>
            (int)((long)Math.Max(0, price) * 95 / 100);

        public static int CalculateGoldSellerProceeds(int price) =>
            Math.Max(0, price) - Math.Max(0, price) / 100;
    }
}
