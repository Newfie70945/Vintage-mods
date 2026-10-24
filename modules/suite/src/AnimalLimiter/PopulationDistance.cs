namespace BlockLogger.AnimalLimiter;

internal static class PopulationDistance
{
    // Preserve the limiter's existing three-dimensional distance calculation.
    // Caller supplies a snapshot local to one synchronous sweep, never a cache
    // across ticks; no entity/player references are retained here.
    internal static double NearestSquared(double x, double y, double z, System.Collections.Generic.List<(double X, double Y, double Z)> players)
    {
        double best = double.MaxValue;
        foreach (var p in players)
        {
            double dx=p.X-x, dy=p.Y-y, dz=p.Z-z;
            double distance=dx*dx+dy*dy+dz*dz;
            if(distance<best)best=distance;
        }
        return best;
    }
}
