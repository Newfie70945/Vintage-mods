using Vintagestory.API.Common.Entities;

namespace BlockLogger.Admin;

public sealed class JailPosition
{
	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int Dimension { get; set; }

	public float Yaw { get; set; }

	public float Pitch { get; set; }

	public EntityPos ToEntityPos()
	{
		EntityPos entityPos = new EntityPos(X, Y, Z, Yaw, Pitch);
		entityPos.Dimension = Dimension;
		return entityPos;
	}

	public static JailPosition From(EntityPos pos)
	{
		return new JailPosition
		{
			X = pos.X,
			Y = pos.Y,
			Z = pos.Z,
			Dimension = pos.Dimension,
			Yaw = pos.Yaw,
			Pitch = pos.Pitch
		};
	}
}
