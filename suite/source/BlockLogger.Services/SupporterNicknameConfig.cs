using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Services;

public sealed class SupporterNicknameConfig
{
	public int SchemaVersion { get; set; } = 1;

	public int CooldownMinutes { get; set; } = 1440;

	public int MinLength { get; set; } = 3;

	public int MaxLength { get; set; } = 20;

	public Dictionary<string, SupporterNicknameRecord> Players { get; set; } = new Dictionary<string, SupporterNicknameRecord>(StringComparer.OrdinalIgnoreCase);

	public void Clamp()
	{
		SchemaVersion = Math.Max(1, SchemaVersion);
		CooldownMinutes = Math.Clamp(CooldownMinutes, 0, 10080);
		MinLength = Math.Clamp(MinLength, 1, 20);
		MaxLength = Math.Clamp(MaxLength, Math.Max(MinLength, 3), 32);
		if (Players == null)
		{
			Dictionary<string, SupporterNicknameRecord> dictionary = (Players = new Dictionary<string, SupporterNicknameRecord>(StringComparer.OrdinalIgnoreCase));
		}
		Dictionary<string, SupporterNicknameRecord> dictionary3 = new Dictionary<string, SupporterNicknameRecord>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, SupporterNicknameRecord> item in Players.Take(10000))
		{
			string text = (item.Key ?? string.Empty).Trim();
			if (text.Length != 0 && item.Value != null)
			{
				string text2 = (item.Value.Nickname ?? string.Empty).Trim();
				if (text2.Length > MaxLength)
				{
					text2 = text2.Substring(0, MaxLength);
				}
				dictionary3[text] = new SupporterNicknameRecord
				{
					Nickname = text2,
					LastChangedUnixMilliseconds = Math.Max(0L, item.Value.LastChangedUnixMilliseconds)
				};
			}
		}
		Players = dictionary3;
	}

	public SupporterNicknameConfig Clone()
	{
		return new SupporterNicknameConfig
		{
			SchemaVersion = SchemaVersion,
			CooldownMinutes = CooldownMinutes,
			MinLength = MinLength,
			MaxLength = MaxLength,
			Players = Players.ToDictionary((KeyValuePair<string, SupporterNicknameRecord> pair) => pair.Key, (KeyValuePair<string, SupporterNicknameRecord> pair) => new SupporterNicknameRecord
			{
				Nickname = pair.Value.Nickname,
				LastChangedUnixMilliseconds = pair.Value.LastChangedUnixMilliseconds
			}, StringComparer.OrdinalIgnoreCase)
		};
	}
}
