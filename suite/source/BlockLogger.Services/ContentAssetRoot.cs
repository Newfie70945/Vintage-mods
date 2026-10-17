using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class ContentAssetRoot
{
	public string Path { get; set; } = "";

	public string Label { get; set; } = "";

	public List<ContentIngredient> Ingredients { get; set; } = new List<ContentIngredient>();
}
