using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace NewfiesSailing;

/// <summary>
/// Visual recovery balloon. The server drives it along a short timeline above a boat: it rises and inflates over the
/// boat, holds while the salvage completes, then floats away and despawns. It has no physics of its own.
/// </summary>
public sealed class EntityRecoveryBalloon : Entity
{
    public const string BoatIdKey = "newfiessailing:balloonBoat";
    public const string OffsetXKey = "newfiessailing:balloonOx";
    public const string OffsetZKey = "newfiessailing:balloonOz";
    public const double HoldHeight = 5;
    public const double LifeSeconds = 13;

    private double age;

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (World.Side != EnumAppSide.Server) return;

        age += dt;
        Entity? boat = World.GetEntityById(WatchedAttributes.GetLong(BoatIdKey, -1));
        if (boat == null || age >= LifeSeconds)
        {
            Die(EnumDespawnReason.Expire);
            return;
        }

        double height = SailingModel.BalloonHeight(age, HoldHeight, LifeSeconds);
        double sway = Math.Sin(age * 1.3 + EntityId) * 0.25;
        ServerPos.SetPos(
            boat.ServerPos.X + WatchedAttributes.GetDouble(OffsetXKey, 0) + sway,
            boat.ServerPos.Y + height,
            boat.ServerPos.Z + WatchedAttributes.GetDouble(OffsetZKey, 0) + sway * 0.6);
        Pos.SetFrom(ServerPos);
    }
}
