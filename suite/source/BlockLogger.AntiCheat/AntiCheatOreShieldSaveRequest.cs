using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatOreShieldSaveRequest
{
	public bool Enabled { get; set; } = true;

	public bool ProtectModdedOres { get; set; } = true;

	public bool RevealOnUnknownNeighbor { get; set; } = false;

	public bool StrictOreMatching { get; set; } = true;

	public int RevealRadiusBlocks { get; set; } = 0;

	public bool RevealWholeVeinOnMine { get; set; } = false;

	public int MaxVeinRevealBlocks { get; set; } = 2048;

	public int DiscoveryRangeBlocks { get; set; } = 96;

	public int VisibilityScanIntervalMs { get; set; } = 250;

	public int MaxLosChecksPerPass { get; set; } = 96;

	public int ViewConeDegrees { get; set; } = 140;

	public int IdleRescanMs { get; set; } = 3000;

	public bool LeakAuditEnabled { get; set; } = true;

	public bool ChunkCacheEnabled { get; set; } = true;

	public int ChunkCacheMiB { get; set; } = 96;

	public int MaxCachedChunks { get; set; } = 4096;

	public List<string> AdditionalOrePatterns { get; set; } = new List<string>();

	public List<string> ExcludedBlockPatterns { get; set; } = new List<string>();
}
