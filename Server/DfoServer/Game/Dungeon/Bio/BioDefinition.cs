using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using PvfLib;

namespace DfoServer.Game.Dungeon.Bio
{
    // Immutable PVF projection. The runtime never reads raw ScriptNode values.
    internal sealed class BioDefinition
    {
        internal int ReadyMilliseconds { get; }
        internal int PlayMilliseconds { get; }
        internal int MinimumPlayers { get; }
        internal int MaximumPlayers { get; }
        internal int LeftSlots { get; }
        internal int RightSlots { get; }
        internal IReadOnlyList<string> ExcludedJobs { get; }
        internal IReadOnlyList<PlayMode> PlayModes { get; }
        internal Points PointRules { get; }
        internal Blind BlindRules { get; }
        internal IReadOnlyList<ItemAmount> RewardsRaw { get; }
        internal IReadOnlyList<ItemAmount> LimitRewardsRaw { get; }
        internal IReadOnlyList<float> ExperienceRaw { get; }
        internal ClearRewardRaw ClearReward { get; }
        internal int LimitRewardRaw { get; }
        internal int ExperienceTicketId { get; }
        internal ItemBox RandomItemBox { get; }
        internal int ZombieChampionLevel { get; }

        private BioDefinition(
            int ready,
            int play,
            int minimumPlayers,
            int maximumPlayers,
            int leftSlots,
            int rightSlots,
            IEnumerable<string> excludedJobs,
            IEnumerable<PlayMode> playModes,
            Points pointRules,
            Blind blindRules,
            IEnumerable<ItemAmount> rewards,
            IEnumerable<ItemAmount> limitRewards,
            IEnumerable<float> experience,
            ClearRewardRaw clearReward,
            int limitReward,
            int experienceTicketId,
            ItemBox randomItemBox,
            int zombieChampionLevel)
        {
            ReadyMilliseconds = ready;
            PlayMilliseconds = play;
            MinimumPlayers = minimumPlayers;
            MaximumPlayers = maximumPlayers;
            LeftSlots = leftSlots;
            RightSlots = rightSlots;
            ExcludedJobs = Freeze(excludedJobs);
            PlayModes = Freeze(playModes);
            PointRules = pointRules;
            BlindRules = blindRules;
            RewardsRaw = Freeze(rewards);
            LimitRewardsRaw = Freeze(limitRewards);
            ExperienceRaw = Freeze(experience);
            ClearReward = clearReward;
            LimitRewardRaw = limitReward;
            ExperienceTicketId = experienceTicketId;
            RandomItemBox = randomItemBox;
            ZombieChampionLevel = zombieChampionLevel;
        }

        private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
            => new ReadOnlyCollection<T>((values ?? throw new ArgumentNullException(nameof(values))).ToArray());

        internal PlayMode SelectMode(int count)
        {
            if (count < MinimumPlayers || count > MaximumPlayers)
                throw new ArgumentOutOfRangeException(nameof(count));
            return PlayModes.First(mode => mode.MaximumPlayers >= count);
        }

        internal static BioDefinition CreateForSelfTest(
            int minimumPlayers = 5,
            int maximumPlayers = 6,
            int readyMilliseconds = 0,
            int playMilliseconds = 10_000)
        {
            if (minimumPlayers < 2 || maximumPlayers < minimumPlayers || maximumPlayers > 8)
                throw new ArgumentOutOfRangeException(nameof(minimumPlayers));
            var mode = new PlayMode(maximumPlayers, 3101, 2, 61657, 61658);
            var items = Enumerable.Range(0, 8)
                .Select(_ => new ItemAmount(10005368, 1))
                .ToArray();
            return new BioDefinition(
                readyMilliseconds,
                playMilliseconds,
                minimumPlayers,
                maximumPlayers,
                maximumPlayers / 2,
                maximumPlayers - maximumPlayers / 2,
                Array.Empty<string>(),
                new[] { mode },
                new Points(100, 5, 100, 50, 50, 2, 500),
                new Blind(0, 80, 20),
                items,
                items,
                Enumerable.Repeat(100f, 8),
                new ClearRewardRaw(10005403, 1, 500),
                4,
                10003806,
                new ItemBox(18303, 300, 300, 120000, 25000),
                16);
        }

        internal static BioDefinition Parse(string modeText, string matchingText)
        {
            if (string.IsNullOrWhiteSpace(modeText) || string.IsNullOrWhiteSpace(matchingText))
                throw new FormatException("Bio PVF definitions cannot be empty.");

            var parser = new ScriptParser();
            var modeRoot = parser.Parse(modeText);
            var matchingNodes = parser.Parse(matchingText)
                .GetChildren("matching info")
                .Where(node => Text(node, "gametype", matchingText) == "zombie")
                .ToArray();
            if (matchingNodes.Length != 1)
                throw new FormatException("Zombie matching definition must be unique.");

            var matching = matchingNodes[0];
            if (Text(matching, "matching bound", matchingText) != "channel"
                || Text(matching, "scene type", matchingText) != "zombie")
                throw new FormatException("Unsupported Zombie matching scope/scene.");

            var minimum = Integer(matching, "min user count", matchingText);
            var maximum = Integer(matching, "max user count", matchingText);
            if (minimum < 2 || maximum < minimum || maximum > 8)
                throw new FormatException("Invalid matching population.");
            var slots = Tokens(Child(matching, "slots"), matchingText);
            if (slots.Count != 4 || slots[0] != "left" || slots[2] != "right")
                throw new FormatException("Invalid Zombie left/right slots.");
            var left = Int(slots[1]);
            var right = Int(slots[3]);
            if (left < 1 || right < 1 || left + right != maximum)
                throw new FormatException("Slot capacity mismatch.");

            var modeValues = Integers(modeRoot, "play mode", modeText);
            if (modeValues.Length == 0 || modeValues.Length % 5 != 0)
                throw new FormatException("Play modes must contain five-value rows.");
            var modes = new List<PlayMode>();
            for (var i = 0; i < modeValues.Length; i += 5)
            {
                var mode = new PlayMode(modeValues[i], modeValues[i + 1], modeValues[i + 2], modeValues[i + 3], modeValues[i + 4]);
                if (mode.MaximumPlayers < minimum || mode.MaximumPlayers > maximum
                    || mode.DungeonId <= 0 || mode.DungeonId > ushort.MaxValue
                    || mode.Field2Raw < 0 || mode.InitialZombieMonsterId <= 0
                    || mode.InfectedZombieMonsterId <= 0
                    || (modes.Count > 0 && mode.MaximumPlayers <= modes[^1].MaximumPlayers))
                    throw new FormatException("Invalid or unordered play-mode row.");
                modes.Add(mode);
            }
            if (modes[^1].MaximumPlayers != maximum)
                throw new FormatException("Missing maximum-population play mode.");

            var pointNode = Child(modeRoot, "point info");
            var points = new Points(
                Integer(pointNode, "damage point", modeText),
                Integer(pointNode, "attack point", modeText),
                Integer(pointNode, "serious attack point", modeText),
                Integer(pointNode, "kill zombie point", modeText),
                Integer(pointNode, "kill human point", modeText),
                Integer(pointNode, "survival point per sec", modeText),
                Integer(pointNode, "last survival point", modeText));
            var blindNode = Child(modeRoot, "human blind");
            var blind = new Blind(
                Integer(blindNode, "base vision", modeText),
                Integer(blindNode, "sub vision", modeText),
                Integer(blindNode, "added stuck", modeText));
            if (new[] { points.Damage, points.Attack, points.SeriousAttack, points.KillZombie,
                        points.KillHuman, points.SurvivalPerSecond, points.LastSurvival,
                        blind.BaseVision, blind.SubVision, blind.AddedStuck }.Any(value => value < 0))
                throw new FormatException("Negative point or blind configuration.");

            var itemBoxNode = Child(modeRoot, "random generation itembox");
            var position = Integers(itemBoxNode, "pos", modeText, 2);
            var itemBox = new ItemBox(
                Integer(itemBoxNode, "index", modeText), position[0], position[1],
                Integer(itemBoxNode, "time after", modeText),
                Integer(itemBoxNode, "make term", modeText));
            if (itemBox.ObjectId <= 0 || itemBox.X < 0 || itemBox.Y < 0
                || itemBox.AfterMilliseconds < 0 || itemBox.IntervalMilliseconds <= 0)
                throw new FormatException("Invalid random item-box configuration.");

            var clearValues = Integers(modeRoot, "clear reward", modeText, 3);
            if (clearValues[0] <= 0 || clearValues[1] < 0 || clearValues[2] < 0)
                throw new FormatException("Invalid clear reward.");
            var experience = Tokens(Child(modeRoot, "reward exp"), modeText)
                .Select(Float).ToArray();
            if (experience.Length != 8 || experience.Any(value => !float.IsFinite(value) || value < 0f))
                throw new FormatException("Eight finite nonnegative raw EXP entries required.");

            var ready = Integer(modeRoot, "ready time", modeText);
            var play = Integer(modeRoot, "play time", modeText);
            var limit = Integer(modeRoot, "limit reward", modeText);
            var ticket = Integer(modeRoot, "exp ticket item", modeText);
            var champion = Integer(modeRoot, "zombie champion level", modeText);
            if (ready < 0 || play <= 0 || limit < 0 || ticket <= 0 || champion < 0)
                throw new FormatException("Invalid duration or reward metadata.");

            return new BioDefinition(
                ready,
                play,
                minimum,
                maximum,
                left,
                right,
                Tokens(Child(matching, "impossible jobs"), matchingText),
                modes,
                points,
                blind,
                ItemRows(modeRoot, "reward item", modeText),
                ItemRows(modeRoot, "reward item limit", modeText),
                experience,
                new ClearRewardRaw(clearValues[0], clearValues[1], clearValues[2]),
                limit,
                ticket,
                itemBox,
                champion);
        }

        private static ScriptNode Child(ScriptNode parent, string tag)
        {
            var children = parent.GetChildren(tag);
            if (children.Count != 1)
                throw new FormatException($"Missing or duplicate [{tag}].");
            return children[0];
        }

        private static List<string> Tokens(ScriptNode node, string source)
            => ScriptValueTokenizer.Tokenize(string.Join(" ", node.DataItems.Select(item => item.GetContent(source))));

        private static string Text(ScriptNode parent, string tag, string source)
        {
            var values = Tokens(Child(parent, tag), source);
            if (values.Count != 1)
                throw new FormatException($"Expected single text in [{tag}].");
            return values[0];
        }

        private static int Int(string token)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Invalid integer: {token}");
            return value;
        }

        private static float Float(string token)
        {
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Invalid float: {token}");
            return value;
        }

        private static int[] Integers(ScriptNode parent, string tag, string source, int count = -1)
        {
            var values = Tokens(Child(parent, tag), source).Select(Int).ToArray();
            if (count >= 0 && values.Length != count)
                throw new FormatException($"Wrong field count in [{tag}].");
            return values;
        }

        private static int Integer(ScriptNode parent, string tag, string source)
            => Integers(parent, tag, source, 1)[0];

        private static ItemAmount[] ItemRows(ScriptNode parent, string tag, string source)
        {
            var values = Integers(parent, tag, source, 16);
            var rows = Enumerable.Range(0, 8)
                .Select(index => new ItemAmount(values[index * 2], values[index * 2 + 1]))
                .ToArray();
            if (rows.Any(row => row.ItemId <= 0 || row.Count < 0))
                throw new FormatException("Invalid rank item table.");
            return rows;
        }
    }

    internal sealed record PlayMode(int MaximumPlayers, int DungeonId, int Field2Raw, int InitialZombieMonsterId, int InfectedZombieMonsterId);
    internal sealed record Points(int Damage, int Attack, int SeriousAttack, int KillZombie, int KillHuman, int SurvivalPerSecond, int LastSurvival);
    internal sealed record Blind(int BaseVision, int SubVision, int AddedStuck);
    internal sealed record ItemAmount(int ItemId, int Count);
    internal sealed record ClearRewardRaw(int ItemId, int Count, int Field2Raw);
    internal sealed record ItemBox(int ObjectId, int X, int Y, int AfterMilliseconds, int IntervalMilliseconds);
}
