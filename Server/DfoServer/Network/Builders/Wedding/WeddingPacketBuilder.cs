using DfoServer.Game.Wedding;
using DfoServer.Game.Characters;
using DfoServer.Network.Parsers.Wedding;
using System;

namespace DfoServer.Network.Builders
{
    internal static class WeddingPacketBuilder
    {
        internal static byte[] StateBody(WeddingState state)
        {
            var w = new GamePacketWriter();
            bool active = state.Status != WeddingStatus.None;
            w.WriteUInt32(active ? checked((uint)state.PairId) : uint.MaxValue);
            w.WriteByte((byte)state.Status);
            w.WriteByte(state.Ceremony);
            w.WriteUInt32(checked((uint)state.MarriedAt));
            w.WriteUInt32(checked((uint)state.Love));
            w.WriteByte(active ? state.Ring : byte.MaxValue);
            return w.ToArray();
        }

        internal static byte[] State(WeddingState state)
            => Notify(NotiPacketTypeA21.WEDDING_INFO, StateBody(state));

        // C94CD0 reads 11 bytes after dispatcher success; C91FD0 reads 12.
        internal static byte[] Result(CmdPacketTypeA21 command, WeddingRequest request, int error, byte? decision = null)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteInt32(ResultError(request, error));
            w.WriteUInt32(request.Peer);
            w.WriteByte(request.Operation);
            w.WriteByte(request.Ceremony);
            w.WriteByte(request.Delay);
            if (decision.HasValue) w.WriteByte(decision.Value);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)command, w.ToArray());
        }

        internal static byte[] Invitation(uint proposer, WeddingRequest request)
        {
            var w = new GamePacketWriter();
            w.WriteInt32(0);
            w.WriteUInt32(proposer);
            w.WriteByte(request.Operation);
            w.WriteByte(request.Ceremony);
            w.WriteByte(request.Delay);
            return Notify(NotiPacketTypeA21.WEDDING_REQUEST, w.ToArray());
        }

        internal static byte[] InvitationResult(uint peer, WeddingRequest request, int error, byte decision)
        {
            var w = new GamePacketWriter();
            w.WriteInt32(ResultError(request, error));
            w.WriteUInt32(peer);
            w.WriteByte(request.Operation);
            w.WriteByte(request.Ceremony);
            w.WriteByte(request.Delay);
            w.WriteByte(decision);
            return Notify(NotiPacketTypeA21.WEDDING_RESPONSE, w.ToArray());
        }

        internal static bool IsRemarryCooldown(WeddingRequest request, int error)
            => request.Operation == 1 && error == (int)WeddingError.Cooldown;

        internal static bool IsCeremonyCooldown(WeddingRequest request, int error)
            => request.Operation == 2 && error == (int)WeddingError.CeremonyCooldown;

        private static int ResultError(WeddingRequest request, int error)
            // A21 maps -151 to "already taken". Its native healing text also
            // hardcodes 06:00, unlike the PVF rolling-day rule. Reject silently
            // with -4 (silent in C94CD0/C91FD0/C91960) and publish the precise
            // reason through SERVER_NOTICE_MESSAGE. Unlike -102, this also
            // closes the proposer's waiting window without an unrelated alert.
            // Op 2 uses a different native switch: -4 would show a generic
            // error, while -106 returns silently in all three result readers.
            => IsRemarryCooldown(request, error) ? -4 : IsCeremonyCooldown(request, error) ? -106 : error;

        internal static byte[] RemarryCooldownNotice(int days)
            => Notify(NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.Build(
                $"您或对方仍处于离婚后的再婚冷却期，需在离婚满{days}天后才能重新订婚。"));

        internal static byte[] CeremonyCooldownNotice(int days)
            => Notify(NotiPacketTypeA21.SERVER_NOTICE_MESSAGE, ServerNoticeMessageBuilder.Build(
                $"您或对方仍处于婚礼预约冷却期，需在上次预约满{days}天后才能再次申请婚礼。"));

        internal static byte[] ItemResult(CmdPacketTypeA21 command, int error)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteInt32(error);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)command, w.ToArray());
        }

        internal static byte[] Ring(byte ring) => Notify(NotiPacketTypeA21.WEDDING_UPGRADE_RING, new[] { ring });

        // C92FF0 consumes error:i32 and ring:u8 whenever the outer flag is one,
        // including failures. Success passes the ring directly to C92960.
        internal static byte[] RingResult(int error, byte ring)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1); w.WriteInt32(error); w.WriteByte(ring);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.WEDDING_UPGRADE_RING, w.ToArray());
        }

        internal static byte[] Partner(WeddingState state, CharacterRecord partner)
        {
            var w = new GamePacketWriter();
            if (state.Status == WeddingStatus.None || partner == null || partner.Deleted || partner.CharacterId != state.PartnerId)
            {
                w.WriteByte(0); w.WriteByte(1);
            }
            else
            {
                w.WriteByte(1);
                w.WriteInt32(0);
                w.WriteUInt32(checked((uint)state.PairId));
                w.WriteUInt32(checked((uint)partner.CharacterId));
                WriteName(w, partner.Name);
                w.WriteByte(partner.Level); w.WriteByte(partner.Job); w.WriteByte(partner.GrowType);
                for (int slot = 0; slot < 13; slot++)
                {
                    var entry = Array.Find(partner.Appearance ?? Array.Empty<CharacterAppearanceEntry>(), x => x.Slot == slot);
                    // C92E10 copies each code into ActorData; 2A48EA4 defaults
                    // these slots to -1. Only -1 selects the job's default
                    // graphics at 17860E7; item code 0 is a metadata lookup.
                    w.WriteInt32(entry?.DisplayItemId is > 0 ? entry.DisplayItemId : -1);
                }
            }
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.WEDDING_CHARAC, w.ToArray());
        }

        internal static byte[] Reservation(WeddingState state, CharacterRecord actor, CharacterRecord partner, ushort invitations)
        {
            var w = new GamePacketWriter();
            w.WriteUInt32(checked((uint)state.PairId));
            w.WriteUInt32(state.Room);
            w.WriteUInt32(checked((uint)state.StartAt));
            w.WriteUInt32(checked((uint)state.ChannelId));
            w.WriteByte(state.Ceremony);
            WriteName(w, actor.Name); WriteName(w, partner.Name);
            w.WriteUInt16(invitations);
            return Notify(NotiPacketTypeA21.WEDDING_CEREMONY, w.ToArray());
        }

        internal static byte[] ActorState(uint actor, WeddingState state)
        {
            var w = new GamePacketWriter();
            w.WriteUInt32(actor);
            w.WriteUInt32(state.Status == WeddingStatus.None ? uint.MaxValue : checked((uint)state.PairId));
            w.WriteByte((byte)state.Status);
            w.WriteByte(state.Status == WeddingStatus.None ? byte.MaxValue : state.Ring);
            w.WriteUInt32(checked((uint)state.MarriedAt));
            return Notify(NotiPacketTypeA21.COUPLE_SIMPLE_INFO, w.ToArray());
        }

        internal static void WriteName(GamePacketWriter writer, byte[] name)
        {
            name ??= Array.Empty<byte>();
            int length = Array.IndexOf(name, (byte)0);
            if (length < 0) length = name.Length;
            if (length > 29) throw new ArgumentException("A21 wedding names must fit its 30-byte NUL-terminated field.");
            writer.WriteBytes(name.AsSpan(0, length).ToArray());
            writer.WriteBytes(new byte[30 - length]);
        }
        internal static byte[] Notify(NotiPacketTypeA21 type, byte[] body)
            => GamePacketEnvelopeBuilder.Build(0, (ushort)type, body);
    }
}
