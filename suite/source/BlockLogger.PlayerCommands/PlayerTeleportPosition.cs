using Vintagestory.API.Common.Entities;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerTeleportPosition
{
	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int Dimension { get; set; }

	public float Yaw { get; set; }

	public float Pitch { get; set; }

	public static PlayerTeleportPosition From(EntityPos pos)
	{
		return new PlayerTeleportPosition
		{
			X = pos.X,
			Y = pos.Y,
			Z = pos.Z,
			Dimension = pos.Dimension,
			Yaw = pos.Yaw,
			Pitch = pos.Pitch
		};
	}

	public EntityPos ToEntityPos()
	{
		EntityPos entityPos = new EntityPos(X, Y, Z, Yaw, Pitch);
		entityPos.Dimension = Dimension;
		return entityPos;
	}
}
