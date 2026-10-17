using System;
using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class CustomPatchSnapshot
{
	public bool Available { get; set; }

	public string HarmonyId { get; set; } = "";

	public string ConfigPath { get; set; } = "";

	public string[] SupportedModes { get; set; } = Array.Empty<string>();

	public List<CustomPatchRow> Patches { get; set; } = new List<CustomPatchRow>();

	public List<CustomPatchSuggestion> Suggestions { get; set; } = new List<CustomPatchSuggestion>();

	public string Note { get; set; } = "";
}
