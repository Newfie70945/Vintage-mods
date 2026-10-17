namespace BlockLogger.Models;

public sealed record EntityActivityAggregate(long WindowStartTicks, long WindowEndTicks, int Action, int EntityTypeId, string EntityCode, string EntityClass, long EventCount, string WorldId, string Dimension, int SampleX, int SampleY, int SampleZ, int RegionX, int RegionZ);
