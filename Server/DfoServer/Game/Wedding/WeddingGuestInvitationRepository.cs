using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingGuestInvitation(WeddingState State, int InviterCharacterId, int GuestCharacterId, long CreatedAt)
    {
        internal long PairId => State.PairId;
        internal long StartAt => State.StartAt;
        internal int LeftCharacterId => State.CharacterId;
        internal int RightCharacterId => State.PartnerId;
    }
    internal sealed record WeddingGuestHostList(WeddingState State, int HostCharacterId, int PerHostQuota,
        int Remaining, IReadOnlyList<WeddingGuestInvitation> Entries)
    {
        internal long PairId => State?.PairId ?? 0;
        internal long StartAt => State?.StartAt ?? 0;
    }
    internal sealed record WeddingGuestInvitationResult(int Error, WeddingGuestHostList Host = null,
        IReadOnlyList<WeddingGuestInvitation> Invitations = null, int Added = 0)
    { internal bool Success => Error == 0; }

    internal sealed class WeddingGuestInvitationRepository
    {
        private readonly IGameDatabase _database;
        private readonly Func<long> _now;
        internal WeddingGuestInvitationRepository(IGameDatabase database, Func<long> now = null)
        { _database = database ?? throw new ArgumentNullException(nameof(database)); _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }

        // Caller captures pair/start before asynchronous roster selection and
        // holds the two hosts' canonical transition gates. No inventory is changed.
        // Error values are domain results; handlers map them to proven A21 codes.
        internal WeddingGuestInvitationResult Invite(int hostCharacterId, int accountId, long pairId, long startAt,
            IReadOnlyList<int> targets, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (hostCharacterId <= 0 || accountId <= 0 || !ValidIdentity(pairId, startAt)
                || targets == null || targets.Count is < 1 or > ushort.MaxValue || targets.Any(x => x <= 0)) return new(-3);
            var wanted = targets.Distinct().ToArray();
            try
            {
                return _database.Write((connection, transaction) =>
                {
                    Need(stillCurrent(), -101);
                    long now = _now();
                    var state = LoadEvent(connection, transaction, pairId, startAt);
                    Need(state != null && state.Status == WeddingStatus.Reserved && now < state.StartAt
                        && (hostCharacterId == state.CharacterId || hostCharacterId == state.PartnerId), -3);
                    var host = WeddingRepository.LoadActor(connection, transaction, hostCharacterId);
                    Need(host != null && !host.Deleted && host.AccountId == accountId && HostsActive(connection, transaction, state), -3);
                    int quota = WeddingCatalog.Get().Ceremonies[state.Ceremony].Invitations;
                    var existing = LoadRows(connection, transaction, state);
                    var byGuest = existing.ToDictionary(x => x.GuestCharacterId);
                    int used = existing.Count(x => x.InviterCharacterId == hostCharacterId);
                    // Validate every target before inserting any of the batch.
                    foreach (int target in wanted)
                    {
                        var guest = WeddingRepository.LoadActor(connection, transaction, target);
                        Need(target != state.CharacterId && target != state.PartnerId && guest != null && !guest.Deleted, -3);
                    }
                    int added = wanted.Count(x => !byGuest.ContainsKey(x));
                    Need(used <= quota && added <= quota - used, -6);
                    Need(now is >= 0 and <= uint.MaxValue, -3);
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO wedding_guest_invitations
(pair_id,ceremony_start_at,guest_character_id,inviter_character_id,created_at) VALUES(@pair,@start,@guest,@host,@now);";
                    command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@start", startAt);
                    command.Parameters.AddWithValue("@host", hostCharacterId); command.Parameters.AddWithValue("@now", now);
                    command.Parameters.AddWithValue("@guest", 0);
                    foreach (int target in wanted)
                    {
                        if (byGuest.ContainsKey(target)) continue;
                        command.Parameters["@guest"].Value = target; command.ExecuteNonQuery();
                    }
                    Need(stillCurrent(), -101);
                    var final = LoadRows(connection, transaction, state);
                    return new WeddingGuestInvitationResult(0,
                        new(state, hostCharacterId, quota, quota - used - added, final),
                        Array.AsReadOnly(final.Where(x => wanted.Contains(x.GuestCharacterId)).ToArray()), added);
                });
            }
            catch (InviteFailure ex) { return new(ex.Error); }
            catch (Exception ex)
            { FileLogger.Log($"[WeddingGuest] invitation transaction failed host={hostCharacterId} pair={pairId} start={startAt}: {ex}"); return new(-102); }
        }

        internal IReadOnlyList<WeddingGuestInvitation> LoadForGuest(int guestCharacterId, long now)
        {
            if (guestCharacterId <= 0) return Array.Empty<WeddingGuestInvitation>();
            return _database.Read<IReadOnlyList<WeddingGuestInvitation>>(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var guest = WeddingRepository.LoadActor(connection, transaction, guestCharacterId);
                if (guest == null || guest.Deleted) return Array.Empty<WeddingGuestInvitation>();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = @"SELECT i.pair_id,i.ceremony_start_at FROM wedding_guest_invitations i
JOIN wedding_pairs p ON p.pair_id=i.pair_id AND p.start_at=i.ceremony_start_at
WHERE i.guest_character_id=@guest AND ((p.status=2 AND @now<p.start_at)
OR (p.status=3 AND @now<p.start_at+@duration)) ORDER BY i.ceremony_start_at,i.pair_id;";
                command.Parameters.AddWithValue("@guest", guestCharacterId);
                command.Parameters.AddWithValue("@now", now); command.Parameters.AddWithValue("@duration", WeddingCatalog.Get().CeremonyEndSeconds);
                var keys = new List<(long Pair, long Start)>();
                using (var reader = command.ExecuteReader()) while (reader.Read()) keys.Add((reader.GetInt64(0), reader.GetInt64(1)));
                var entries = new List<WeddingGuestInvitation>();
                foreach (var key in keys)
                {
                    var entry = FindForScope(connection, transaction, guestCharacterId, guest.AccountId, key.Pair, key.Start, now);
                    if (entry != null) entries.Add(entry);
                }
                transaction.Commit(); return entries.AsReadOnly();
            });
        }

        internal WeddingGuestHostList LoadForHost(int hostCharacterId, long now) => _database.Read(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var owner = WeddingRepository.LoadActor(connection, transaction, hostCharacterId);
            var hostState = WeddingRepository.Load(connection, transaction, hostCharacterId);
            var state = LoadEvent(connection, transaction, hostState.PairId, hostState.StartAt);
            if (owner == null || owner.Deleted || !Active(state, now) || !HostsActive(connection, transaction, state))
                return new WeddingGuestHostList(null, hostCharacterId, 0, 0, Array.Empty<WeddingGuestInvitation>());
            var rows = LoadRows(connection, transaction, state);
            int quota = WeddingCatalog.Get().Ceremonies[state.Ceremony].Invitations;
            int used = rows.Count(x => x.InviterCharacterId == hostCharacterId);
            int remaining = state.Status == WeddingStatus.Reserved && now < state.StartAt ? Math.Max(0, quota - used) : 0;
            transaction.Commit(); return new WeddingGuestHostList(state, hostCharacterId, quota, remaining, rows);
        });

        internal WeddingState LoadEvent(long pairId, long startAt) => _database.Read(connection =>
        { using var transaction = connection.BeginTransaction(deferred: true); var state = LoadEvent(connection, transaction, pairId, startAt); transaction.Commit(); return state; });
        internal IReadOnlyList<WeddingGuestInvitation> LoadForCeremony(long pairId, long startAt) => _database.Read(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = LoadForCeremony(connection, transaction, pairId, startAt);
            transaction.Commit(); return result;
        });
        internal static IReadOnlyList<WeddingGuestInvitation> LoadForCeremony(SqliteConnection connection,
            SqliteTransaction transaction, long pairId, long startAt)
        {
            var state = LoadEvent(connection, transaction, pairId, startAt);
            return state == null ? Array.Empty<WeddingGuestInvitation>() : LoadRows(connection, transaction, state);
        }
        // Historical recipients are useful for invalidating letters after a
        // terminal event. These IDs are never entry or asset authorization.
        internal IReadOnlyList<int> LoadGuestIdsForCeremony(long pairId, long startAt) => _database.Read(connection =>
            LoadGuestIdsForCeremony(connection, null, pairId, startAt));
        internal static IReadOnlyList<int> LoadGuestIdsForCeremony(SqliteConnection connection, SqliteTransaction transaction,
            long pairId, long startAt)
        {
            if (!ValidIdentity(pairId, startAt)) return Array.Empty<int>();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT guest_character_id FROM wedding_guest_invitations WHERE pair_id=@pair AND ceremony_start_at=@start ORDER BY guest_character_id;";
            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@start", startAt);
            var ids = new List<int>(); using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt32(0)); return ids.AsReadOnly();
        }

        // This is event context, not admission permission. Callers performing
        // recovery/cancellation still need to observe an event with a deleted host.
        internal static WeddingState LoadEvent(SqliteConnection connection, SqliteTransaction transaction, long pairId, long startAt)
        {
            if (!ValidIdentity(pairId, startAt)) return null;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT left_character_id FROM wedding_pairs WHERE pair_id=@pair AND start_at=@start AND status IN (2,3);";
            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@start", startAt);
            var value = command.ExecuteScalar(); if (value == null || value is DBNull) return null;
            var left = WeddingRepository.Load(connection, transaction, Convert.ToInt32(value));
            var right = WeddingRepository.Load(connection, transaction, left.PartnerId);
            return left.PairId == pairId && left.StartAt == startAt && WeddingRules.IsReciprocal(left, right) ? left : null;
        }

        // New entry/payment stop at the native invitation window's start deadline.
        internal static WeddingGuestInvitation FindForPayment(SqliteConnection connection, SqliteTransaction transaction,
            int guestCharacterId, int accountId, long pairId, long startAt, long now)
        {
            var entry = FindForScope(connection, transaction, guestCharacterId, accountId, pairId, startAt, now);
            return entry?.State.Status == WeddingStatus.Reserved && now < startAt ? entry : null;
        }
        // Invitation component of entry authority only. The scene/entry owner
        // MUST additionally require the matching financial paid receipt.
        internal WeddingGuestInvitation FindForChurch(int guestCharacterId, int accountId, long pairId, long startAt,
            int channelId, byte area, long now,
            Func<SqliteConnection, SqliteTransaction, WeddingGuestInvitation, bool> additionalAuthority = null) => _database.Read(connection =>
        {
            if (!WeddingSceneCatalog.Get().CeremonyRooms.Any(x => x.Area == area)) return null;
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = FindForPayment(connection, transaction, guestCharacterId, accountId, pairId, startAt, now);
            if (result != null && additionalAuthority != null && !additionalAuthority(connection, transaction, result)) result = null;
            transaction.Commit();
            return result != null && result.State.ChannelId == channelId && result.State.Room == area ? result : null;
        });
        // The outer scene owner supplies financial authorization as a predicate
        // so invitation and paid receipt can share this exact read snapshot.
        internal WeddingGuestInvitation FindForArea(int guestCharacterId, int accountId, int channelId, byte area, long now,
            bool allowActiveCeremony = false,
            Func<SqliteConnection, SqliteTransaction, WeddingGuestInvitation, bool> additionalAuthority = null)
            => _database.Read(connection =>
        {
            if (guestCharacterId <= 0 || accountId <= 0 || channelId < 0
                || !WeddingSceneCatalog.Get().CeremonyRooms.Any(x => x.Area == area)) return null;
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT pair_id,start_at FROM wedding_pairs WHERE channel_id=@channel AND room=@area AND status IN (2,3);";
            command.Parameters.AddWithValue("@channel", channelId); command.Parameters.AddWithValue("@area", area);
            long pair = 0, start = 0;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                pair = reader.GetInt64(0); start = reader.GetInt64(1);
                if (reader.Read()) return null;
            }
            var result = allowActiveCeremony
                ? FindForScope(connection, transaction, guestCharacterId, accountId, pair, start, now)
                : FindForPayment(connection, transaction, guestCharacterId, accountId, pair, start, now);
            if (result != null && additionalAuthority != null && !additionalAuthority(connection, transaction, result)) result = null;
            transaction.Commit(); return result;
        });
        internal static WeddingGuestInvitation FindForScope(SqliteConnection connection, SqliteTransaction transaction,
            int guestCharacterId, int accountId, long pairId, long startAt, long now)
        {
            var state = LoadEvent(connection, transaction, pairId, startAt);
            if (!Active(state, now) || !HostsActive(connection, transaction, state)
                || guestCharacterId == state.CharacterId || guestCharacterId == state.PartnerId) return null;
            var guest = WeddingRepository.LoadActor(connection, transaction, guestCharacterId);
            if (guest == null || guest.Deleted || guest.AccountId != accountId) return null;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT inviter_character_id,created_at FROM wedding_guest_invitations
WHERE pair_id=@pair AND ceremony_start_at=@start AND guest_character_id=@guest;";
            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@start", startAt); command.Parameters.AddWithValue("@guest", guestCharacterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            int inviter = reader.GetInt32(0);
            return inviter == state.CharacterId || inviter == state.PartnerId
                ? new WeddingGuestInvitation(state, inviter, guestCharacterId, reader.GetInt64(1)) : null;
        }

        private static IReadOnlyList<WeddingGuestInvitation> LoadRows(SqliteConnection connection, SqliteTransaction transaction, WeddingState state)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT guest_character_id,inviter_character_id,created_at FROM wedding_guest_invitations
WHERE pair_id=@pair AND ceremony_start_at=@start ORDER BY created_at,guest_character_id;";
            command.Parameters.AddWithValue("@pair", state.PairId); command.Parameters.AddWithValue("@start", state.StartAt);
            var entries = new List<WeddingGuestInvitation>(); using var reader = command.ExecuteReader();
            while (reader.Read()) entries.Add(new(state, reader.GetInt32(1), reader.GetInt32(0), reader.GetInt64(2)));
            return entries.AsReadOnly();
        }
        private static bool HostsActive(SqliteConnection connection, SqliteTransaction transaction, WeddingState state)
        {
            if (state == null) return false;
            var left = WeddingRepository.LoadActor(connection, transaction, state.CharacterId);
            var right = WeddingRepository.LoadActor(connection, transaction, state.PartnerId);
            return left != null && right != null && !left.Deleted && !right.Deleted;
        }
        private static bool Active(WeddingState state, long now) => state != null && now >= 0
            && (state.Status == WeddingStatus.Reserved && now < state.StartAt
                || state.Status == WeddingStatus.Ceremony && now < checked(state.StartAt + WeddingCatalog.Get().CeremonyEndSeconds));
        private static bool ValidIdentity(long pair, long start) => pair is > 0 and < uint.MaxValue && start is > 0 and <= uint.MaxValue;
        private static void Need(bool condition, int error) { if (!condition) throw new InviteFailure(error); }
        private sealed class InviteFailure : Exception { internal int Error { get; } internal InviteFailure(int error) => Error = error; }
    }
}
