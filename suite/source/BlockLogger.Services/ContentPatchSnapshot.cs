using System;
using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class ContentPatchSnapshot
{
	public bool Available { get; set; }

	public string ConfigPath { get; set; } = "";

	public string GeneratedModPath { get; set; } = "";

	public bool RestartRequired { get; set; }

	public string[] SupportedOperations { get; set; } = Array.Empty<string>();

	public List<ContentPatchDefinition> Patches { get; set; } = new List<ContentPatchDefinition>();

	public string Note { get; set; } = "";
}
