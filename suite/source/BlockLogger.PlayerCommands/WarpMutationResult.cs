namespace BlockLogger.PlayerCommands;

public sealed class WarpMutationResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public WarpSnapshot Snapshot { get; set; } = new WarpSnapshot();
}
