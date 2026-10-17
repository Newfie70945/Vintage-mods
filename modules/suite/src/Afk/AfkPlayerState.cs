using Vintagestory.API.Common;

namespace BlockLogger.Afk;

public class AfkPlayerState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public EnumGameMode OriginalGameMode { get; set; } = EnumGameMode.Survival;
    public long EnteredAfkUnixMs { get; set; }
    public bool AutoKickWarningSent { get; set; }
}
