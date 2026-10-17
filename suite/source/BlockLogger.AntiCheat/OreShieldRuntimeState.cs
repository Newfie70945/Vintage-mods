using System;
using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldRuntimeState
{
	public Dictionary<string, Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells>> Discoveries { get; } = new Dictionary<string, Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells>>(StringComparer.Ordinal);

	public HashSet<string> SyncReadyPlayerUids { get; } = new HashSet<string>(StringComparer.Ordinal);
}
