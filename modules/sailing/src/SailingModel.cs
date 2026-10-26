namespace NewfiesSailing;

/// <summary>
/// Pure sailing maths with no game dependencies so it can be unit tested by tests/Checks.
/// Wind vectors are the direction the wind blows TOWARD, in world X/Z (north is -Z).
/// </summary>
public enum SailTrim { Furled = 0, Reefed = 1, Full = 2 }

public static class SailingModel
{
    private static readonly string[] BeaufortNames =
    {
        "Calm", "Light air", "Light breeze", "Gentle breeze", "Moderate breeze", "Fresh breeze",
        "Strong breeze", "Near gale", "Gale", "Strong gale", "Storm", "Violent storm", "Hurricane force"
    };

    // Lower bound of each Beaufort force in knots (force 0..12).
    private static readonly double[] BeaufortKnots = { 0, 1, 4, 7, 11, 17, 22, 28, 34, 41, 48, 56, 64 };

    // Wave height in blocks-ish (metres) per force, for flavour text.
    private static readonly double[] WaveHeight = { 0, 0.1, 0.2, 0.6, 1, 2, 3, 4, 5.5, 7, 9, 11.5, 14 };

    // (angle off the wind in degrees, sail efficiency 0..1): in irons, close-hauled, close reach, beam, broad, run.
    private static readonly (double Angle, double Eff)[] PolarPoints =
    {
        (0, 0.0), (30, 0.05), (45, 0.45), (70, 0.80), (90, 1.00), (120, 0.95), (150, 0.80), (180, 0.65)
    };

    public static int Beaufort(double knots)
    {
        int force = 0;
        for (int i = 0; i < BeaufortKnots.Length; i++) if (knots >= BeaufortKnots[i]) force = i;
        return force;
    }

    public static string BeaufortName(int force) => BeaufortNames[Math.Clamp(force, 0, 12)];

    public static double WaveHeightFor(int force) => WaveHeight[Math.Clamp(force, 0, 12)];

    /// <summary>Compass bearing (0 = north, 90 = east) the wind blows FROM.</summary>
    public static double WindFromBearing(double windX, double windZ)
    {
        if (windX * windX + windZ * windZ < 1e-12) return 0;
        double toward = Math.Atan2(windX, -windZ) * 180.0 / Math.PI;
        return (toward + 180.0 + 360.0) % 360.0;
    }

    public static string Compass(double bearing)
    {
        string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return names[(int)Math.Round(((bearing % 360.0) + 360.0) % 360.0 / 45.0) % 8];
    }

    /// <summary>Angle between the boat's course and the direction the wind comes FROM: 0 = head to wind, 180 = dead run.</summary>
    public static double AngleOffWind(double motionX, double motionZ, double windX, double windZ)
    {
        double m = Math.Sqrt(motionX * motionX + motionZ * motionZ);
        double w = Math.Sqrt(windX * windX + windZ * windZ);
        if (m < 1e-9 || w < 1e-9) return 90;
        double cos = Math.Clamp((motionX * windX + motionZ * windZ) / (m * w), -1, 1);
        // cos = 1 when sailing with the wind (running = 180 degrees off the wind source, head to wind = 0).
        return 180.0 - Math.Acos(cos) * 180.0 / Math.PI;
    }

    public static double PointOfSailEfficiency(double angleOffWind)
    {
        double a = Math.Clamp(angleOffWind, 0, 180);
        for (int i = 1; i < PolarPoints.Length; i++)
        {
            if (a <= PolarPoints[i].Angle)
            {
                var (a0, e0) = PolarPoints[i - 1];
                var (a1, e1) = PolarPoints[i];
                return e0 + (e1 - e0) * (a - a0) / (a1 - a0);
            }
        }
        return PolarPoints[^1].Eff;
    }

    public static string PointOfSailName(double angleOffWind) => angleOffWind switch
    {
        < 35 => "in irons (head to wind)",
        < 60 => "close-hauled",
        < 80 => "close reach",
        < 110 => "beam reach",
        < 150 => "broad reach",
        _ => "running"
    };

    /// <summary>
    /// Gust multiplier around 1.0 built from two incommensurate sines, so gusts feel irregular but are deterministic.
    /// </summary>
    public static double Gust(double seconds, double amplitude, double phase = 0)
    {
        double n = 0.6 * Math.Sin(seconds / 7.3 + phase) + 0.4 * Math.Sin(seconds / 2.9 + phase * 1.7);
        return Math.Max(0, 1 + amplitude * n);
    }

    /// <summary>
    /// Speed multiplier (relative to the boat's unmodified speed) for the given wind and point of sail.
    /// Becalmed boats crawl, drive scales with wind up to a reference force, and above storm force the crew reefs and slows.
    /// </summary>
    public static double SpeedMultiplier(double knots, double angleOffWind, double minMultiplier, double maxMultiplier, double referenceKnots, double overloadKnots)
        => SpeedMultiplier(knots, angleOffWind, minMultiplier, maxMultiplier, referenceKnots, overloadKnots, 1.0);

    /// <summary>
    /// Same as above for a given sail area (see <see cref="SailArea"/>). Wind load is knots times area, so a reefed
    /// boat is slower in light air but stays under the overload threshold in a gale. Furled sails barely drive the boat.
    /// </summary>
    public static double SpeedMultiplier(double knots, double angleOffWind, double minMultiplier, double maxMultiplier, double referenceKnots, double overloadKnots, double sailArea)
    {
        double eff = PointOfSailEfficiency(angleOffWind);
        double load = knots * sailArea;
        double drive = Math.Clamp(load / Math.Max(1, referenceKnots), 0, 1.25);
        double m = minMultiplier + (maxMultiplier - minMultiplier) * eff * drive;
        if (load > overloadKnots)
        {
            double over = Math.Clamp((load - overloadKnots) / Math.Max(1, overloadKnots * 0.5), 0, 1);
            m *= 1 - 0.6 * over;
        }
        if (sailArea <= 0.1) m *= 0.4;
        return Math.Clamp(m, 0.05, maxMultiplier);
    }

    public static double SailArea(SailTrim trim) => trim switch { SailTrim.Furled => 0.05, SailTrim.Reefed => 0.55, _ => 1.0 };

    public static bool TryParseTrim(string? text, out SailTrim trim)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "full": trim = SailTrim.Full; return true;
            case "reef": case "reefed": trim = SailTrim.Reefed; return true;
            case "furl": case "furled": trim = SailTrim.Furled; return true;
            default: trim = SailTrim.Full; return false;
        }
    }

    /// <summary>True when the sail is carrying more wind load than it should (time to reef).</summary>
    public static bool Overpowered(double knots, double sailArea, double overloadKnots) => knots * sailArea > overloadKnots;

    /// <summary>Heel angle magnitude in radians: greatest on a beam reach under full sail in strong wind, nil head to wind or running.</summary>
    public static double Heel(double knots, double angleOffWind, double sailArea, double referenceKnots, double maxHeel = 0.5)
    {
        double load = Math.Clamp(knots * sailArea / Math.Max(1, referenceKnots * 1.5), 0, 1.2);
        return maxHeel * load * Math.Sin(Math.Clamp(angleOffWind, 0, 180) * Math.PI / 180.0);
    }

    /// <summary>Which side the wind comes from relative to the course: +1 starboard-side wind source, -1 port, 0 if undefined.</summary>
    public static int WindSide(double motionX, double motionZ, double windX, double windZ)
    {
        double cross = motionX * windZ - motionZ * windX;
        return cross > 1e-12 ? 1 : cross < -1e-12 ? -1 : 0;
    }

    /// <summary>Fraction of boat speed lost sideways as leeway: most when close-hauled, none when running.</summary>
    public static double LeewayFraction(double angleOffWind, double sailArea, double drive01, double strength = 0.15)
        => strength * sailArea * Math.Clamp(drive01, 0, 1) * Math.Clamp(1 - angleOffWind / 150.0, 0, 1);

    /// <summary>
    /// Squall wind multiplier: 1 outside the squall, ramping up over the first quarter, holding the peak, then
    /// easing off over the last third. <paramref name="age"/> is seconds since the squall began.
    /// </summary>
    public static double SquallFactor(double age, double duration, double peak)
    {
        if (age <= 0 || age >= duration || duration <= 0) return 1;
        double t = age / duration;
        double k = t < 0.25 ? t / 0.25 : t > 0.67 ? (1 - t) / 0.33 : 1;
        return 1 + (peak - 1) * Math.Clamp(k, 0, 1);
    }

    /// <summary>Hull pitch and roll in radians for a boat in a sea of the given Beaufort force. Phase decorrelates boats.</summary>
    public static (double Pitch, double Roll) Bob(double seconds, int force, double phase = 0)
    {
        double f = Math.Clamp(force, 0, 12);
        double rollAmp = Math.Min(0.5, 0.012 + 0.036 * f);
        double pitchAmp = rollAmp * 0.6;
        double period = Math.Max(2.2, 5.0 - 0.25 * f);
        double w = 2 * Math.PI / period;
        double roll = rollAmp * (0.75 * Math.Sin(w * seconds + phase) + 0.25 * Math.Sin(1.9 * w * seconds + phase * 2.3));
        double pitch = pitchAmp * (0.75 * Math.Sin(0.83 * w * seconds + phase * 1.3) + 0.25 * Math.Sin(2.3 * w * seconds + phase));
        return (pitch, roll);
    }

    /// <summary>Splash particles to emit per visual tick for a given force (none in light airs).</summary>
    public static int SplashCount(int force, double boatSpeed)
    {
        if (force < 3) return 0;
        double n = (force - 2) * 1.5 + Math.Min(boatSpeed, 0.3) * 20;
        return (int)Math.Min(40, Math.Round(n));
    }

    // ---- Temporal storms at sea ----

    /// <summary>Slowly wandering wind rotation (radians) used while a temporal storm scrambles the weather.</summary>
    public static double TemporalWindRotation(double seconds, double amplitude = 1.5)
        => amplitude * (0.6 * Math.Sin(seconds / 9.1) + 0.4 * Math.Sin(seconds / 3.7 + 1.3));

    public static (double X, double Z) RotateWind(double x, double z, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return (x * c - z * s, x * s + z * c);
    }

    /// <summary>Compass error in degrees while a temporal storm spins the needle.</summary>
    public static double CompassNoise(double seconds)
        => 40 * (0.5 * Math.Sin(seconds / 1.7) + 0.5 * Math.Sin(seconds / 0.9 + 2));

    /// <summary>Beaufort force as felt on the water: a temporal storm adds two forces of unnatural sea.</summary>
    public static int StormForce(int force, bool temporalStorm) => temporalStorm ? Math.Min(12, force + 2) : force;

    // ---- Anchor ----

    /// <summary>0 when the anchor holds, up to 1 when it is dragging badly (wind far above the holding limit).</summary>
    public static double AnchorDrag(double knots, double holdKnots)
        => knots <= holdKnots ? 0 : Math.Clamp((knots - holdKnots) / Math.Max(1, holdKnots), 0, 1);

    // ---- Weather glass ----

    /// <summary>Barometer reading in hPa. It falls ahead of a squall, bottoms out in one and swings wildly in a temporal storm.</summary>
    public static double Barometer(double? secondsToSquall, bool squallActive, bool temporalStorm, double seconds)
    {
        double slow = 1.5 * Math.Sin(seconds / 600.0);
        if (temporalStorm) return 1000 + 25 * Math.Sin(seconds / 2.1) + slow;
        if (squallActive) return 982 + slow;
        double drop = secondsToSquall is double eta ? 28 * Math.Clamp(1 - eta / 180.0, 0, 1) : 0;
        return 1013 - drop + slow;
    }

    public static string Trend(double now, double thirtySecondsLater)
    {
        double d = thirtySecondsLater - now;
        return d < -3 ? "falling rapidly" : d < -0.8 ? "falling" : d > 0.8 ? "rising" : "steady";
    }

    public static string GlassForecast(double hPa) => hPa switch
    {
        < 990 => "storm conditions",
        < 1000 => "squall imminent",
        < 1008 => "unsettled weather ahead",
        _ => "fair"
    };

    // ---- Wake ----

    /// <summary>Foam particles per visual tick trailing the stern at the given speed (blocks/tick).</summary>
    public static int WakeCount(double speed)
        => speed < 0.01 ? 0 : (int)Math.Min(14, Math.Round(2 + Math.Min(speed, 0.25) * 40));

    // ---- Wear and repair ----

    public const double RepairPerItem = 25;

    /// <summary>Hull and sail wear per second while a boat is under way. Gales, overpowered sail and temporal storms chew boats up.</summary>
    public static (double Hull, double Sail) WearPerSecond(int force, bool overpowered, bool temporalStorm, double sailArea)
    {
        double hull = 0.015 + (force >= 6 ? (force - 5) * 0.05 : 0) + (temporalStorm ? 0.1 : 0);
        double sail = 0.008 + (overpowered ? 0.45 : 0) + (force >= 8 ? (force - 7) * 0.08 * sailArea : 0);
        return (hull, sail);
    }

    /// <summary>Speed factor from boat condition (0..100 each): ragged sails lose drive, a leaking hull wallows.</summary>
    public static double ConditionSpeedFactor(double hull, double sail)
    {
        double sailF = 0.55 + 0.45 * Math.Clamp(sail / 100.0, 0, 1);
        double hullF = hull >= 30 ? 1 : hull <= 0 ? 0.3 : 0.3 + 0.7 * hull / 30.0;
        return sailF * hullF;
    }

    public static string ConditionLabel(double value) => value switch
    {
        >= 85 => "excellent",
        >= 60 => "good",
        >= 35 => "worn",
        >= 10 => "poor",
        _ => "failing"
    };

    // ---- Seasons ----

    /// <summary>Seasonal wind strength: summer doldrums, autumn and especially winter gales.</summary>
    public static double SeasonWindFactor(string? season) => (season ?? "").ToLowerInvariant() switch
    {
        "winter" => 1.25,
        "fall" or "autumn" => 1.1,
        "summer" => 0.85,
        _ => 1.0
    };

    // ---- Swamping and capsizing ----

    /// <summary>Water shipped per second: a leaky hull ships water even in calm, rough seas slop over the gunwale.</summary>
    public static double FloodPerSecond(int force, double hull)
    {
        double leak = hull < 50 ? (1 - Math.Max(0, hull) / 50.0) * 0.8 : 0;
        double slop = force >= 7 ? (force - 6) * 0.12 : 0;
        return leak + slop;
    }

    /// <summary>Water that drains or evaporates per second in settled conditions with a sound hull.</summary>
    public static double NaturalDrainPerSecond(int force, double hull) => force < 5 && hull >= 50 ? 0.03 : 0;

    /// <summary>Waterlogged boats wallow: no penalty under 40% water, down to a quarter speed when full.</summary>
    public static double WaterSpeedFactor(double water) => 1 - 0.75 * Math.Clamp((water - 40) / 60.0, 0, 1);

    /// <summary>Probability per second of capsizing: only when badly overloaded, worst beam-on, never under reefed or furled sail.</summary>
    public static double CapsizeChancePerSecond(double knots, double sailArea, double angleOffWind, double overloadKnots, double severity = 1)
    {
        double threshold = Math.Max(1, overloadKnots * 1.4);
        double load = knots * sailArea;
        if (load <= threshold) return 0;
        double ratio = load / threshold - 1;
        double beam = Math.Sin(Math.Clamp(angleOffWind, 0, 180) * Math.PI / 180.0);
        return Math.Clamp(0.08 * ratio * (0.3 + 0.7 * beam) * severity, 0, 0.5);
    }

    // ---- Recovery balloons ----

    /// <summary>
    /// Height of a recovery balloon above its boat after <paramref name="age"/> seconds: eases up to the hold height over
    /// 3 s, hovers, then climbs away over the last 5 s of its life.
    /// </summary>
    public static double BalloonHeight(double age, double holdHeight, double life)
    {
        if (age <= 0) return 0;
        if (age < 3) { double t = age / 3.0; return holdHeight * (1 - (1 - t) * (1 - t)); }
        double leave = life - 5;
        if (age < leave) return holdHeight + 0.2 * Math.Sin((age - 3) * 2.0);
        double u = Math.Clamp((age - leave) / 5.0, 0, 1);
        return holdHeight + 15 * u * u;
    }

    // ---- Rougher seas ----

    /// <summary>Beating into big seas costs speed: from Force 5, up to 40% lost head to wind, nothing running.</summary>
    public static double HeadSeaFactor(int force, double angleOffWind)
    {
        if (force < 5) return 1;
        double into = Math.Clamp(1 - angleOffWind / 90.0, 0, 1);
        return 1 - Math.Min(0.4, 0.05 * (force - 4)) * into;
    }

    /// <summary>Chance per second that a rogue wave slams a boat under way: none below Force 7, rising with the sea.</summary>
    public static double RogueWaveChancePerSecond(int force) => force < 7 ? 0 : Math.Min(0.06, (force - 6) * 0.006);

    /// <summary>Water shipped and hull damage from a rogue wave. Reefed (small) sail area leaves the boat slightly better trimmed.</summary>
    public static (double Water, double Hull) RogueWaveImpact(int force, double sailArea)
    {
        double k = Math.Clamp((force - 6) / 6.0, 0.2, 1.0);
        return (10 + 20 * k, 2 + 5 * k * (0.7 + 0.3 * sailArea));
    }

    // ---- Fog banks ----

    /// <summary>Fog intensity 0..1: builds over the first fifth, holds, then burns off over the last quarter.</summary>
    public static double FogIntensity(double age, double duration)
    {
        if (age <= 0 || age >= duration || duration <= 0) return 0;
        double t = age / duration;
        double k = t < 0.2 ? t / 0.2 : t > 0.75 ? (1 - t) / 0.25 : 1;
        return Math.Clamp(k, 0, 1);
    }

    /// <summary>Approximate visibility in blocks at the given fog intensity.</summary>
    public static double FogVisibility(double intensity) => 300 - 260 * Math.Clamp(intensity, 0, 1);

    /// <summary>Fog sits on light air: wind strength multiplier (1 clear, 0.6 in dense fog).</summary>
    public static double FogWindFactor(double intensity) => 1 - 0.4 * Math.Clamp(intensity, 0, 1);

    // ---- Crew morale ----

    /// <summary>Morale change per second: fair weather and a sound boat lift the crew; gales, storms, leaks and fog sap them.</summary>
    public static double MoraleChangePerSecond(int force, bool temporalStorm, double fog, double hull, double water)
    {
        double d = 0;
        if (force <= 4 && hull >= 60 && water < 30 && !temporalStorm) d += 0.15;
        if (force >= 7) d -= (force - 6) * 0.12;
        if (temporalStorm) d -= 0.3;
        if (hull < 30) d -= 0.1;
        if (water >= 60) d -= 0.1;
        return d - 0.05 * Math.Clamp(fog, 0, 1);
    }

    /// <summary>A cheerful crew pulls harder: 0.88 at zero morale up to 1.0.</summary>
    public static double MoraleSpeedFactor(double morale) => 0.88 + 0.12 * Math.Clamp(morale / 100.0, 0, 1);

    public static string MoraleLabel(double morale) => morale switch
    {
        >= 80 => "cheerful",
        >= 55 => "steady",
        >= 30 => "grumbling",
        >= 10 => "close to mutiny",
        _ => "mutinous"
    };

    // ---- Hull heave ----

    /// <summary>Vertical swell motion in blocks (experimental; the game's buoyancy may fight it).</summary>
    public static double Heave(double seconds, int force, double phase = 0)
    {
        double f = Math.Clamp(force, 0, 12);
        double amp = Math.Min(0.35, 0.02 * f);
        double w = 2 * Math.PI / Math.Max(2.5, 5.5 - 0.3 * f);
        return amp * (0.7 * Math.Sin(w * seconds + phase) + 0.3 * Math.Sin(1.7 * w * seconds + phase * 2));
    }
}
