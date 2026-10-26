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
    /// <summary>Wind load (knots x sail area) above which a boat is overpowered and slows. Reefing keeps the load down.</summary>
    public double OverloadKnots { get; set; } = 25;
    /// <summary>Fraction of speed lost sideways close-hauled (0 disables leeway).</summary>
    public double LeewayStrength { get; set; } = 0.15;
    public bool SquallsEnabled { get; set; } = true;
    public int SquallMinIntervalSeconds { get; set; } = 300;
    public int SquallMaxIntervalSeconds { get; set; } = 900;
    public int SquallMinDurationSeconds { get; set; } = 30;
    public int SquallMaxDurationSeconds { get; set; } = 75;
    public double SquallPeakMultiplier { get; set; } = 2.1;
    public int SquallWarningSeconds { get; set; } = 25;
    public bool TemporalStormsEnabled { get; set; } = true;
    /// <summary>Max radians the wind direction wanders either side during a temporal storm.</summary>
    public double TemporalStormWindRotation { get; set; } = 1.5;
    public double TemporalStormWindBoost { get; set; } = 1.3;
    public bool BoardersEnabled { get; set; } = true;
    /// <summary>Entity spawned beside boats in a temporal storm. Set empty to disable.</summary>
    public string BoarderEntityCode { get; set; } = "game:drifter-normal";
    public int MaxBoarders { get; set; } = 3;
    public int BoarderCooldownSeconds { get; set; } = 45;
    /// <summary>Wind (knots) an anchor can hold against before it drags.</summary>
    public double AnchorHoldKnots { get; set; } = 35;
    public double GustAmplitude { get; set; } = 0.35;
    public int TickMilliseconds { get; set; } = 100;
    /// <summary>Speed (blocks/tick) below which a boat counts as stationary and is not modified.</summary>
    public double MinMotion { get; set; } = 0.004;
    public double SpeedSmoothing { get; set; } = 0.15;
    public bool WarnOnForceChange { get; set; } = true;
    public int WarnCooldownSeconds { get; set; } = 30;
}

/// <summary>Client-only visual settings (ModConfig/NewfiesSailingClient.json).</summary>
public sealed class SailingVisualsConfig
{
    public bool BobbingEnabled { get; set; } = true;
    public bool SplashesEnabled { get; set; } = true;
    /// <summary>Multiplier on hull roll/pitch; 0 disables, 2 doubles.</summary>
    public double BobIntensity { get; set; } = 1.0;
    public double KnotsPerWindUnit { get; set; } = 30;
    public string[] BoatCodeContains { get; set; } = { "sail" };
    public float Range { get; set; } = 48;
    public bool HeelingEnabled { get; set; } = true;
    public bool WakeEnabled { get; set; } = true;
    public bool StormGlowEnabled { get; set; } = true;
    public double ReferenceKnots { get; set; } = 17;
    /// <summary>Set to -1 if boats lean INTO the wind instead of away from it.</summary>
    public int HeelDirection { get; set; } = 1;
}
