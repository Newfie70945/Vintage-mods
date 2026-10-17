using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Services;

public sealed class ContentPatchStore
{
	public int SchemaVersion { get; set; } = 1;

	public List<ContentPatchDefinition> Patches { get; set; } = new List<ContentPatchDefinition>();

	public void Clamp()
	{
		if (Patches == null)
		{
			List<ContentPatchDefinition> list = (Patches = new List<ContentPatchDefinition>());
		}
		Patches = Patches.Where((ContentPatchDefinition x) => x != null).Take(200).ToList();
		foreach (ContentPatchDefinition patch in Patches)
		{
			patch.Clamp();
		}
	}
}
