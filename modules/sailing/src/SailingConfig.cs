namespace NewfiesSailing;

public sealed class SailingConfig
{
    /// <summary>Knots per game wind unit (VS wind speed is roughly 0..1+).</summary>
    public double KnotsPerWindUnit { get; set; } = 30;
    /// <summary>Entity code substrings that identify boats the mod controls. Rafts are left alone by default.</summary>
    public string[] BoatCodeContains { get; set; } = { "sail" };
    public double MinSpeedMultiplier { get; set; } = 0.35;
    public double MaxSpeedMultiplier { get; set; } = 1.35;
    public double ReferenceKnots { get; set; } = 17;
    public double StormKnots { get; set; } = 41;
    public double GustAmplitude { get; set; } = 0.35;
    public int TickMilliseconds { get; set; } = 100;
    /// <summary>Speed (blocks/tick) below which a boat counts as stationary and is not modified.</summary>
    public double MinMotion { get; set; } = 0.004;
    public double SpeedSmoothing { get; set; } = 0.15;
    public bool WarnOnForceChange { get; set; } = true;
    public int WarnCooldownSeconds { get; set; } = 30;
}
