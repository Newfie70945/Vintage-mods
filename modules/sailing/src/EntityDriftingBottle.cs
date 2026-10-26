using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>A message in a bottle adrift on the water. Holds a note, and often a treasure chart.</summary>
public sealed class EntityDriftingBottle : EntityDriftingThing
{
    private static readonly string[] Notes =
    {
        "\"Day 41. The fog took the mate and the compass both. If you read this, tell Marla I kept my word.\"",
        "\"Never sail beyond the grey water when the sky tears. The things that climb aboard are not men.\"",
        "\"Reef early, reef often. Cap'n Hollis learned it the wet way.\"",
        "\"Fair winds to whoever finds this. The anchor holds better in sand than in kelp.\"",
        "\"We sang our way through the worst of it. A concertina is worth more than a second pump.\"",
        "\"The tide runs east on the flood and west on the ebb. Plan your crossings by it.\""
    };

    protected override double LifeSeconds => 1500;
    protected override double DriftSpeed => 0.8;

    protected override void Salvage(IServerPlayer player)
    {
        string note = Notes[World.Rand.Next(Notes.Length)];
        string text = "[Sailing] You fish out a sealed bottle. Inside, a note: " + note;
        if (World.Rand.NextDouble() < 0.6 && World.Api.ModLoader.GetModSystem<SailingModSystem>()?.GiveChart(player) == true)
            text += "\nA rolled treasure chart is tucked beside it.";
        player.SendMessage(GlobalConstants.InfoLogChatGroup, text, EnumChatType.Notification);
        Die(EnumDespawnReason.Removed);
    }
}
