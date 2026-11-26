using System;
using System.IO;
using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon.Bio
{
    internal static class BioDefinitionCatalog
    {
        internal static BioDefinition LoadCurrent()
        {
            var mode = PvfArchiveAccessor.ReadText("etc/gamemode/zombiemode.etc");
            var matching = PvfArchiveAccessor.ReadText("etc/MatchingMode.etc");
            return BioDefinition.Parse(mode, matching);
        }

        internal static bool TryLoadCurrent(out BioDefinition definition, out string error)
        {
            definition = null;
            error = null;
            try
            {
                definition = LoadCurrent();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
