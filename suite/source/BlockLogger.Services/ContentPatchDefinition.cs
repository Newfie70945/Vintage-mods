namespace BlockLogger.Services;

public sealed class ContentPatchDefinition
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public bool Enabled { get; set; }

	public string File { get; set; } = "";

	public string Operation { get; set; } = "add";

	public string Path { get; set; } = "/";

	public string FromPath { get; set; } = "";

	public string ValueJson { get; set; } = "null";

	public string Side { get; set; } = "server";

	public string DependsOnModId { get; set; } = "";

	public bool InvertDependency { get; set; }

	public string Notes { get; set; } = "";

	public void Clamp()
	{
		Id = (Id ?? "").Trim();
		Name = Limit((Name ?? "").Trim(), 120);
		File = Limit((File ?? "").Trim(), 500);
		Operation = Limit((Operation ?? "add").Trim(), 20);
		Path = Limit((Path ?? "/").Trim(), 800);
		FromPath = Limit((FromPath ?? "").Trim(), 800);
		ValueJson = Limit(string.IsNullOrWhiteSpace(ValueJson) ? "null" : ValueJson.Trim(), 131072);
		Side = Limit((Side ?? "server").Trim().ToLowerInvariant(), 16);
		DependsOnModId = Limit((DependsOnModId ?? "").Trim().ToLowerInvariant(), 128);
		Notes = Limit((Notes ?? "").Trim(), 1200);
	}

	private static string Limit(string value, int max)
	{
		return (value.Length <= max) ? value : value.Substring(0, max);
	}

	public ContentPatchDefinition Clone()
	{
		return new ContentPatchDefinition
		{
			Id = Id,
			Name = Name,
			Enabled = Enabled,
			File = File,
			Operation = Operation,
			Path = Path,
			FromPath = FromPath,
			ValueJson = ValueJson,
			Side = Side,
			DependsOnModId = DependsOnModId,
			InvertDependency = InvertDependency,
			Notes = Notes
		};
	}
}
