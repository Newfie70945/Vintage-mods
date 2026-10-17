using System;

namespace BlockLogger.AntiCheat;

[Flags]
internal enum PacketGuardTrustScope
{
	None = 0,
	Movement = 1,
	Reach = 2,
	All = Movement | Reach
}
