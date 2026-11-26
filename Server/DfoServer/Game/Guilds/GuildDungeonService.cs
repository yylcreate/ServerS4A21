using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Guilds
{
    internal sealed record GuildDungeonPrerequisite(int DungeonId, int GaugeLimit, int GaugeOnClear, int RequiredRatio);

    internal sealed record GuildDungeonClearContribution(Guid SourceEventId, int MonthId, int MemberCount)
    {
        internal IReadOnlyList<GuildDungeonContributionParticipant> Participants { get; init; }
    }

    internal sealed record GuildDungeonProjection(int GuildId, int MonthId, uint ResetEndAtSeconds,
        IReadOnlyDictionary<int, int> ClearCounts);

    // Immutable admission facts shared by the physical instance, not a second
    // guild/player cache. Durable progress belongs to the repository/outbox.
    internal sealed class GuildDungeonAdmissionSnapshot
    {
        internal GuildDungeonAdmissionSnapshot(int guildId, int sourceCharacterId, int sourceAccountId,
            int dungeonId, byte difficulty, int gaugeLimit, int gaugeOnClear,
            IReadOnlyList<GuildDungeonPrerequisite> prerequisites, int monthId = 0,
            IReadOnlyList<GuildDungeonGaugeReward> rewards = null)
        {
            GuildId = guildId;
            SourceCharacterId = sourceCharacterId;
            SourceAccountId = sourceAccountId;
            DungeonId = dungeonId;
            Difficulty = difficulty;
            GaugeLimit = gaugeLimit;
            GaugeOnClear = gaugeOnClear;
            MonthId = GuildDungeonMonth.ResolveMonthId(monthId);
            Prerequisites = Array.AsReadOnly(prerequisites.ToArray());
            Rewards = Array.AsReadOnly((rewards ?? Array.Empty<GuildDungeonGaugeReward>()).ToArray());
        }
        internal int GuildId { get; }
        internal int SourceCharacterId { get; }
        internal int SourceAccountId { get; }
        internal int DungeonId { get; }
        internal byte Difficulty { get; }
        internal int GaugeLimit { get; }
        internal int GaugeOnClear { get; }
        internal int MonthId { get; }
        internal IReadOnlyList<GuildDungeonPrerequisite> Prerequisites { get; }
        internal IReadOnlyList<GuildDungeonGaugeReward> Rewards { get; }
        internal GuildDungeonAdmissionSnapshot ForParticipant(int characterId, int accountId)
            => new(GuildId, characterId, accountId, DungeonId, Difficulty, GaugeLimit, GaugeOnClear, Prerequisites, MonthId, Rewards);
    }

    internal sealed class GuildDungeonCatalog
    {
        internal const string ConfigPath = "etc/guild/guilddungeon/0_2way6dungeon.etc";
        private static readonly Lazy<GuildDungeonCatalog> CurrentDefinition = new(Load);
        internal static GuildDungeonCatalog Current => CurrentDefinition.Value;
        internal bool IsValid { get; private init; }
        internal string Error { get; private init; }
        internal GuildDungeonConfigFile Config { get; private init; }
        internal int WorldMapAreaId { get; private init; }
        private HashSet<int> ManagedDungeonIds { get; init; } = new();
        internal bool IsManagedDungeon(int dungeonId) => ManagedDungeonIds.Contains(dungeonId);
        internal IReadOnlyDictionary<int, GuildDungeonEntry> Rules { get; private init; }
            = new System.Collections.ObjectModel.ReadOnlyDictionary<int, GuildDungeonEntry>(new Dictionary<int, GuildDungeonEntry>());

        private static GuildDungeonCatalog Load()
        {
            var managed = new HashSet<int>();
            try
            {
                var towns = LstFile.Parse(PvfArchiveAccessor.ReadText("town/town.lst"));
                var guildTown = towns.GetById(GuildAgitAccessPolicy.TownId);
                if (guildTown == null)
                    throw new InvalidOperationException("Guild town is not registered.");
                var town = TownFile.Parse(PvfArchiveAccessor.ReadText("town/" + guildTown.FilePath));
                var gates = town.Areas.Where(area => string.Equals(area.AreaType, "dungeon gate", StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var gate in gates)
                {
                    var directory = WorldMap.GetAreaById(gate.LinkedId);
                    if (directory == null) throw new InvalidOperationException("Guild gate directory is unavailable.");
                    foreach (var row in directory.Dungeons) managed.Add(row.DungeonId);
                }
                var config = GuildDungeonConfigFile.Parse(PvfArchiveAccessor.ReadText(ConfigPath));
                foreach (var rule in config.Dungeons) managed.Add(rule.DungeonId);
                if (!config.IsValid || config.Dungeons.Count == 0)
                    throw new InvalidOperationException(config.InvalidReason);
                var areas = new HashSet<int>();
                foreach (var rule in config.Dungeons)
                {
                    if (rule.GaugeLimit <= 0 || rule.GaugeOnClear <= 0
                        || rule.ConquestGaugeRatio < 0 || rule.ConquestGaugeRatio > 100)
                        throw new InvalidOperationException("Guild purification rule is outside the supported consumer range: " + rule.DungeonId);
                    GuildDungeonRewardRules.Select(rule, 0);
                    GuildDungeonRewardRules.Select(rule, int.MaxValue);
                    var dungeon = GameWorld.Dungeon.GetDungeonFile(rule.DungeonId);
                    var area = WorldMap.GetAreaByDungeonId(rule.DungeonId);
                    if (dungeon?.GuildDungeon != true || !dungeon.GuildDungeonDefinitionValid
                        || dungeon.Mazes.Count == 0 || dungeon.DesignateDungeonDifficulty?.Length != 1
                        || dungeon.DesignateDungeonDifficulty[0] < 0 || dungeon.DesignateDungeonDifficulty[0] > byte.MaxValue
                        || area == null)
                        throw new InvalidOperationException("Guild dungeon registration is incomplete: " + rule.DungeonId);
                    areas.Add(area.AreaId);
                }
                if (areas.Count != 1)
                    throw new InvalidOperationException("Guild rules do not share one registered world-map area.");
                var worldMapAreaId = areas.Single();
                var registered = WorldMap.GetAreaById(worldMapAreaId).Dungeons.Select(row => row.DungeonId).OrderBy(id => id);
                if (!registered.SequenceEqual(config.Dungeons.Select(row => row.DungeonId).OrderBy(id => id)))
                    throw new InvalidOperationException("Guild world-map directory and conquest rules differ.");
                return new GuildDungeonCatalog
                {
                    IsValid = true, Config = config, WorldMapAreaId = worldMapAreaId, ManagedDungeonIds = managed,
                    Rules = new System.Collections.ObjectModel.ReadOnlyDictionary<int, GuildDungeonEntry>(
                        config.Dungeons.ToDictionary(rule => rule.DungeonId))
                };
            }
            catch (Exception exception)
            {
                FileLogger.Log("[GuildDungeon] resource definition closed: " + exception.Message);
                return new GuildDungeonCatalog { Error = exception.Message, ManagedDungeonIds = managed };
            }
        }
    }

    internal sealed class GuildDungeonService
    {
        private readonly IGameDatabase _database;
        private readonly GuildDungeonProgressRepository _progress;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly int? _unlockRatio;
        // The normal policy uses each prerequisite's PVF threshold, as does
        // the client's local button gate. Overrides are explicit test inputs.
        internal GuildDungeonService(IGameDatabase database, Func<DateTimeOffset> utcNow = null, int? unlockRatio = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _progress = new GuildDungeonProgressRepository(database);
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            if (unlockRatio.HasValue && (unlockRatio < 1 || unlockRatio > 100))
                throw new ArgumentOutOfRangeException(nameof(unlockRatio));
            _unlockRatio = unlockRatio;
        }
        internal GuildDungeonCatalog Catalog => GuildDungeonCatalog.Current;
        internal int CurrentMonthId => GuildDungeonMonth.MonthId(_utcNow());
        internal int RequiredRatio(GuildDungeonEntry rule) => _unlockRatio ?? rule.ConquestGaugeRatio;
        internal IReadOnlyDictionary<int, int> LoadForMember(int characterId, out int guildId)
            => _progress.LoadForMember(characterId, out guildId, CurrentMonthId);

        internal GuildDungeonProjection LoadProjectionForMember(int characterId)
        {
            var now = _utcNow();
            int monthId = GuildDungeonMonth.MonthId(now);
            var counts = _progress.LoadForMember(characterId, out int guildId, monthId);
            return new GuildDungeonProjection(guildId, monthId,
                guildId > 0 ? GuildDungeonMonth.NextResetUnixSeconds(now) : 0, counts);
        }

        internal bool TryPrepare(int characterId, int accountId, int dungeonId, byte difficulty,
            int sourceTownId, int sourceAreaId, out GuildDungeonAdmissionSnapshot admission, out string error)
        {
            admission = null;
            error = null;
            var dungeon = GameWorld.Dungeon.GetDungeonFile(dungeonId);
            if (dungeon == null || !dungeon.GuildDungeonDefinitionValid)
            { error = "guild_marker_unavailable"; return false; }
            if (dungeon.GuildDungeon != true && !Catalog.IsManagedDungeon(dungeonId))
                return true;
            if (dungeon.GuildDungeon != true || !Catalog.IsValid || !Catalog.Rules.TryGetValue(dungeonId, out var rule)
                || sourceTownId != GuildAgitAccessPolicy.TownId
                || !Town.TryGetDungeonGateReturnInfo(sourceTownId, sourceAreaId, out var gate)
                || gate.WorldMapAreaId != Catalog.WorldMapAreaId
                || dungeon.DesignateDungeonDifficulty[0] != difficulty)
            { error = "guild_dungeon_route_or_difficulty_unavailable"; return false; }
            int monthId = CurrentMonthId;
            var candidate = _database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var guild = GuildRepository.GetForMember(connection, transaction, characterId);
                if (guild == null) return null;
                var required = rule.RequiredDungeonConquest.Select(id => Catalog.Rules[id])
                    .Select(prerequisite => new GuildDungeonPrerequisite(prerequisite.DungeonId,
                        prerequisite.GaugeLimit, prerequisite.GaugeOnClear, RequiredRatio(prerequisite))).ToArray();
                var result = new GuildDungeonAdmissionSnapshot(guild.Id, characterId, accountId,
                    dungeonId, difficulty, rule.GaugeLimit, rule.GaugeOnClear, required, monthId, rule.Rewards);
                return GuildDungeonProgressRepository.ValidateAdmission(connection, transaction, characterId, result, monthId)
                    ? result : null;
            });
            if (candidate == null)
            { error = "guild_membership_or_conquest_required"; return false; }
            admission = candidate;
            return true;
        }

        internal bool ValidateParticipant(int characterId, int accountId, GuildDungeonAdmissionSnapshot admission)
            => admission != null && _database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                return GuildDungeonProgressRepository.ValidateAdmission(connection, transaction, characterId,
                    admission.ForParticipant(characterId, accountId), CurrentMonthId);
            });
    }
}
