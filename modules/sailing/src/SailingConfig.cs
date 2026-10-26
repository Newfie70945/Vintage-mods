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
    public bool FogEnabled { get; set; } = true;
    public int FogMinIntervalSeconds { get; set; } = 600;
    public int FogMaxIntervalSeconds { get; set; } = 1500;
    public int FogMinDurationSeconds { get; set; } = 120;
    public int FogMaxDurationSeconds { get; set; } = 240;
    public bool MoraleEnabled { get; set; } = true;
    public bool CurrentsEnabled { get; set; } = true;
    /// <summary>Full-strength current push in blocks per tick added to boat motion.</summary>
    public double CurrentPush { get; set; } = 0.003;
    /// <summary>Strongest current in knots, for reports.</summary>
    public double MaxCurrentKnots { get; set; } = 2.0;
    /// <summary>When false (default) only boats with a rider nearby are carried by the current; anchor to stay put either way.</summary>
    public bool CurrentsAffectEmptyBoats { get; set; } = false;
    public bool BottlesEnabled { get; set; } = true;
    public int BottleIntervalSeconds { get; set; } = 900;
    public int MaxBottles { get; set; } = 2;
    public bool WrecksEnabled { get; set; } = true;
    public int MaxWrecks { get; set; } = 2;
    public int WreckSpawnCooldownSeconds { get; set; } = 40;
    public bool RogueWavesEnabled { get; set; } = true;
    /// <summary>Multiplier on rogue wave water and hull damage.</summary>
    public double RogueWaveStrength { get; set; } = 1.0;
    /// <summary>Speed lost beating into rough seas (false disables).</summary>
    public bool HeadSeasEnabled { get; set; } = true;
    public bool WearEnabled { get; set; } = true;
    public bool SwampingEnabled { get; set; } = true;
    public bool CapsizingEnabled { get; set; } = true;
    /// <summary>Multiplier on capsize probability (0 effectively disables).</summary>
    public double CapsizeSeverity { get; set; } = 1.0;
    /// <summary>Multiplier on all wear rates (0.5 = half as fast).</summary>
    public double WearRate { get; set; } = 1.0;
    public bool SeasonalWindEnabled { get; set; } = true;
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
    /// <summary>Extra multiplier on the rough-sea roll and pitch (1 = previous behaviour, 1.5 = rougher).</summary>
    public double SeaRoughness { get; set; } = 1.5;
    public bool FogVisualsEnabled { get; set; } = true;
    public bool AmbientSoundsEnabled { get; set; } = true;
    /// <summary>Spyglass zoom: fraction of the normal field of view.</summary>
    public double SpyglassZoom { get; set; } = 0.22;
    /// <summary>Experimental vertical swell motion; the game's buoyancy may fight it, so it is off by default.</summary>
    public bool HeaveEnabled { get; set; } = false;
    public double KnotsPerWindUnit { get; set; } = 30;
    public string[] BoatCodeContains { get; set; } = { "sail" };
    public float Range { get; set; } = 48;
    public bool HeelingEnabled { get; set; } = true;
    public bool WakeEnabled { get; set; } = true;
    /// <summary>Show overturned and waterlogged boats (flip upside down, bow dips).</summary>
    public bool CapsizeVisualsEnabled { get; set; } = true;
    public bool StormGlowEnabled { get; set; } = true;
    public double ReferenceKnots { get; set; } = 17;
    /// <summary>Set to -1 if boats lean INTO the wind instead of away from it.</summary>
    public int HeelDirection { get; set; } = 1;
}
