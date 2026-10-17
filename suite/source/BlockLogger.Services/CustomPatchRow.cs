namespace BlockLogger.Services;

public sealed class CustomPatchRow
{
	public CustomPatchDefinition Definition { get; set; } = new CustomPatchDefinition();

	public long Calls { get; set; }

	public long Skipped { get; set; }

	public double AverageMilliseconds { get; set; }

	public double MaxMilliseconds { get; set; }
}
