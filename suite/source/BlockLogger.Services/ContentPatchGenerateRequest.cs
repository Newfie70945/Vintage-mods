namespace BlockLogger.Services;

public sealed class ContentPatchGenerateRequest
{
	public bool Guided { get; set; }

	public string Id { get; set; } = "";

	public string RootPath { get; set; } = "";

	public bool Activate { get; set; }

	public string Notes { get; set; } = "";

	public string Preset { get; set; } = "generic";

	public string Name { get; set; } = "";

	public string File { get; set; } = "";

	public string Operation { get; set; } = "add";

	public string Path { get; set; } = "/";

	public string FromPath { get; set; } = "";

	public string ValueJson { get; set; } = "null";

	public string Side { get; set; } = "server";

	public string DependsOnModId { get; set; } = "";

	public bool InvertDependency { get; set; }

	public string IngredientKey { get; set; } = "A";

	public int Quantity { get; set; } = 1;

	public string Code { get; set; } = "game:stick";

	public bool Enabled { get; set; } = true;
}
