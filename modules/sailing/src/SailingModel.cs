namespace NewfiesSailing;

/// <summary>
/// Pure sailing maths with no game dependencies so it can be unit tested by tests/Checks.
/// Wind vectors are the direction the wind blows TOWARD, in world X/Z (north is -Z).
/// </summary>
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
    public static double SpeedMultiplier(double knots, double angleOffWind, double minMultiplier, double maxMultiplier, double referenceKnots, double stormKnots)
    {
        double eff = PointOfSailEfficiency(angleOffWind);
        double drive = Math.Clamp(knots / Math.Max(1, referenceKnots), 0, 1.25);
        double m = minMultiplier + (maxMultiplier - minMultiplier) * eff * drive;
        if (knots > stormKnots)
        {
            double over = Math.Clamp((knots - stormKnots) / Math.Max(1, stormKnots * 0.5), 0, 1);
            m = m * (1 - 0.6 * over);
        }
        return Math.Clamp(m, 0.05, maxMultiplier);
    }

    /// <summary>Hull pitch and roll in radians for a boat in a sea of the given Beaufort force. Phase decorrelates boats.</summary>
    public static (double Pitch, double Roll) Bob(double seconds, int force, double phase = 0)
    {
        double f = Math.Clamp(force, 0, 12);
        double rollAmp = Math.Min(0.38, 0.008 + 0.028 * f);
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
}
