namespace BlockLogger.Models;

/// <summary>Compact summary for high-volume entity spawn/despawn activity.</summary>
public sealed record EntityActivityAggregate(
    long WindowStartTicks,
    long WindowEndTicks,
    int Action,
    int EntityTypeId,
    string EntityCode,
    string EntityClass,
    long EventCount,
    string WorldId,
    string Dimension,
    int SampleX,
    int SampleY,
    int SampleZ,
    int RegionX,
    int RegionZ);
