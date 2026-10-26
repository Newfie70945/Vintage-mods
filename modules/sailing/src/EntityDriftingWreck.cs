using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>A derelict wreck adrift on the water, usually found in fog. Salvaged once for loot, with a chance of a treasure chart.</summary>
public sealed class EntityDriftingWreck : EntityDriftingThing
{
    public const string LogKey = "newfiessailing:log:wrecks";

    // item code, min, max, chance
    private static readonly (string Code, int Min, int Max, double Chance)[] Loot =
    {
        ("game:gear-rusty", 1, 3, 0.9),
        ("game:flaxtwine", 2, 5, 0.8),
        ("game:ingot-copper", 1, 2, 0.5),
        ("game:plank-oak", 2, 4, 0.7),
        ("game:ingot-iron", 1, 1, 0.2),
        ("game:feather", 1, 3, 0.4)
    };

    protected override double LifeSeconds => 1200;

    protected override void Salvage(IServerPlayer player)
    {
        int found = SailingLoot.Spawn(World, Loot, ServerPos.XYZ.AddCopy(0, 1, 0));
        player.Entity.WatchedAttributes.SetInt(LogKey, player.Entity.WatchedAttributes.GetInt(LogKey, 0) + 1);
        string text = found > 0
            ? "[Sailing] You pick through the rotting wreck and find a few useful scraps before it slips beneath the waves."
            : "[Sailing] The wreck is picked clean. It slips beneath the waves.";
        if (World.Rand.NextDouble() < 0.15 && World.Api.ModLoader.GetModSystem<SailingModSystem>()?.GiveChart(player) == true)
            text += " Wedged in a seam you find a salt-stained treasure chart!";
        player.SendMessage(GlobalConstants.InfoLogChatGroup, text, EnumChatType.Notification);
        World.PlaySoundAt(new AssetLocation("newfiessailing:sounds/waveslam"), ServerPos.X, ServerPos.Y, ServerPos.Z, null, true, 32, 0.8f);
        Die(EnumDespawnReason.Removed);
    }
}
