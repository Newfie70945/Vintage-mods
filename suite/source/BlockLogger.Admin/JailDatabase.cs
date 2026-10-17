using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class JailDatabase
{
	public int SchemaVersion { get; set; } = 1;

	public JailPosition? Jail { get; set; }

	public List<JailSentence> Sentences { get; set; } = new List<JailSentence>();
}
