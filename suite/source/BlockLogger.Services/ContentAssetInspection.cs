using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class ContentAssetInspection
{
	public string File { get; set; } = "";

	public string Kind { get; set; } = "";

	public bool FieldsTruncated { get; set; }

	public List<ContentAssetRoot> Roots { get; set; } = new List<ContentAssetRoot>();

	public List<ContentAssetField> Fields { get; set; } = new List<ContentAssetField>();
}
