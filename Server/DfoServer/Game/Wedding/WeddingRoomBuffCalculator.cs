using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingRoomBuffEvaluation(int Decoration, WeddingRoomBuff Buff);

    internal static class WeddingRoomBuffCalculator
    {
        // A21 C81DF0 iterates installed slots and sums the second dword of
        // each 8-byte item-metadata entry at +1BCh. The material-list mapping
        // is verified separately from this arithmetic; the supplied callback
        // owns that mapping. Stored furniture never contributes to this total.
        internal static WeddingRoomBuffEvaluation Calculate(WeddingRoomState room, WeddingRoomCatalog furniture,
            WeddingRoomBuffCatalog buffs, Func<int, int> decorationForItem)
        {
            ArgumentNullException.ThrowIfNull(room); ArgumentNullException.ThrowIfNull(furniture);
            ArgumentNullException.ThrowIfNull(buffs); ArgumentNullException.ThrowIfNull(decorationForItem);
            int decoration = 0;
            var values = new Dictionary<int, int>();
            for (int slot = 0; slot < room.Installed.Length; slot++)
            {
                int item = room.Installed[slot];
                if (item == 0) continue;
                if (!furniture.FurniturePositions.TryGetValue(item, out var positions) || !positions.Contains(slot))
                    throw new InvalidDataException("Installed furniture has no corresponding A21 PVF position.");
                if (!values.TryGetValue(item, out int value))
                {
                    value = decorationForItem(item);
                    if (value < 0) throw new InvalidDataException("A21 furniture decoration cannot be negative.");
                    values.Add(item, value);
                }
                try { decoration = checked(decoration + value); }
                catch (OverflowException ex) { throw new InvalidDataException("A21 furniture decoration exceeds the native signed field.", ex); }
            }
            return new(decoration, buffs.Evaluate(decoration));
        }
    }
}
