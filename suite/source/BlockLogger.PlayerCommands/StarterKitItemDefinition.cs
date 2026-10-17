namespace BlockLogger.PlayerCommands;

public sealed class StarterKitItemDefinition
{
	public int Slot { get; set; }

	public string StackBase64 { get; set; } = "";

	public string Code { get; set; } = "";

	public int StackSize { get; set; }
}
