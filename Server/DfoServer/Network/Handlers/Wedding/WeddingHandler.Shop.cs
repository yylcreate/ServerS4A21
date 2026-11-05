using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        // Return false only for an ordinary product. Invalid wedding requests
        // are consumed here so they cannot fall through to the main inventory.
        internal async Task<bool> TryBuyItemAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return false;
            int itemId = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (!WeddingShopPricing.IsWeddingProduct(itemId)) return false;
            // Native 23E7160 emits item/count/shop/NPC as four signed dwords.
            if (body.Length != 16 || !WeddingShopPricing.TryResolve(itemId, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)), out var quote, out bool furniture))
            { await session.SendPacketAsync(WeddingShopPacketBuilder.Failure(-2)); return true; }
            int cid = session.Player?.CharacterId ?? 0;
            if (!Current(session, cid) || !TownReady(session))
            { await session.SendPacketAsync(WeddingShopPacketBuilder.Failure(-2)); return true; }
            var state = _repository.Load(cid);
            if (state.Status != WeddingStatus.Married)
            { await session.SendPacketAsync(WeddingShopPacketBuilder.Failure(-2)); return true; }
            bool ran = false;
            await WithPair(session, state.PartnerId, async (actor, partner, peer) =>
            {
                ran = true;
                var result = new WeddingRoomCommitService(WeddingRoomCatalog.Get()).Commit(actor, partner, state.PartnerId, quote,
                    () => Current(session, cid) && TownReady(session) && (peer == null || Current(peer, state.PartnerId)));
                if (!Current(session, cid)) return;
                if (!result.Success)
                { await session.SendPacketAsync(WeddingShopPacketBuilder.Failure(result.Error)); return; }
                await session.SendPacketAsync(WeddingShopPacketBuilder.Success(result, itemId, quote.Count, furniture));
                if (!Current(session, cid)) return;
                // The client uses the reciprocal love projection for its next
                // local price check. Publication occurs after the purchase ACK.
                await Publish(session, state.PartnerId, 9, false);
                if (Current(session, cid)) await PublishRoomAsync(session, state.PartnerId, result, WeddingRoomOperation.Buy);
                var quest = session.GameSession?.QuestManager;
                if (quest != null && Current(session, cid))
                {
                    var mutations = new List<InventoryMutationResult>();
                    if (result.BuyMutation != null) mutations.Add(result.BuyMutation);
                    foreach (var cost in quote.Materials) mutations.Add(new InventoryMutationResult { ItemTemplateId = cost.ItemId });
                    await quest.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(actor, mutations);
                }
            });
            if (!ran && Current(session, cid)) await session.SendPacketAsync(WeddingShopPacketBuilder.Failure(-2));
            return true;
        }
    }
}
