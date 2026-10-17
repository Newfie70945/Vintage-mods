using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Services;

public sealed class CustomPatchStore
{
	public int SchemaVersion { get; set; } = 1;

	public List<CustomPatchDefinition> Patches { get; set; } = new List<CustomPatchDefinition>();

	public void Clamp()
	{
		if (Patches == null)
		{
			List<CustomPatchDefinition> list = (Patches = new List<CustomPatchDefinition>());
		}
		Patches = Patches.Where((CustomPatchDefinition x) => x != null).Take(100).ToList();
		foreach (CustomPatchDefinition patch in Patches)
		{
			patch.Clamp();
		}
	}
}
