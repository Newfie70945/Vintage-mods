namespace BlockLogger.Services;

public sealed class SupporterNicknameRecord
{
	public string Nickname { get; set; } = string.Empty;

	public long LastChangedUnixMilliseconds { get; set; }
}
