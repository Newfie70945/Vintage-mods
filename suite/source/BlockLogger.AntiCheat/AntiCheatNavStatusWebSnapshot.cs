namespace BlockLogger.AntiCheat;

public sealed class AntiCheatNavStatusWebSnapshot
{
	public bool Enabled { get; set; }

	public bool Available { get; set; }

	public int Suspicious { get; set; }

	public int Review { get; set; }

	public int MissingOrTimedOut { get; set; }

	public AntiCheatNavPacketGuardWebSnapshot PacketGuard { get; set; } = new AntiCheatNavPacketGuardWebSnapshot();
}
