using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BlockLogger.Integrations;
using Newfies.ChatProtocol;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class ChatDisplayService : IDisposable
{
	private sealed record ModerationLine(string Raw, ChatMarkdown.Result Result, long CreatedTicks)
	{
		public string? EmittedBody { get; init; }

		public string? EmittedLine { get; init; }
	}

	private sealed class ParsedPlayerChatLine
	{
		public string Body { get; init; } = string.Empty;

		public string PlayerNameColor { get; init; } = string.Empty;
	}

	public const string QueryEvent = "newfiesse:chat-display-query";

	public const string ChangedEvent = "newfiesse:chat-display-changed";

	public const string ModerationTextQueryEvent = "newfiesse:chat-moderation-text-query";

	public const string PresentationQueryEvent = "newfiesse:chat-presentation-query";

	private readonly Dictionary<(string Uid, int Channel), ModerationLine> moderationLines = new Dictionary<(string, int), ModerationLine>();

	public const string SenderQueryEvent = "newfiesse:chat-sender-query";

	private long lastChatFailureTicks;

	private readonly ICoreServerAPI api;

	private readonly System.Func<IPlayerRole?, string>? resolveConfiguredNameColor;

	private readonly System.Func<string, string, string>? resolveDisplayName;

	private ChatDisplayConfig config;

	private bool registered;

	private bool disposed;

	public ChatDisplayConfig Config => config;

	public ChatDisplayService(ICoreServerAPI api, System.Func<IPlayerRole?, string>? resolveConfiguredNameColor = null, System.Func<string, string, string>? resolveDisplayName = null)
	{
		this.api = api;
		this.resolveConfiguredNameColor = resolveConfiguredNameColor;
		this.resolveDisplayName = resolveDisplayName;
		config = ChatDisplayConfig.Load(api);
	}

	public void Start()
	{
		if (!registered)
		{
			registered = true;
			api.Event.PlayerChat += OnPlayerChat;
			api.Event.RegisterEventBusListener(OnChatDisplayQuery, 0.5, "newfiesse:chat-display-query");
			api.Event.RegisterEventBusListener(OnChatSenderQuery, 0.5, "newfiesse:chat-sender-query");
			api.Event.RegisterEventBusListener(OnModerationTextQuery, 0.5, "newfiesse:chat-moderation-text-query");
			api.Event.RegisterEventBusListener(OnPresentationQuery, 0.5, "newfiesse:chat-presentation-query");
			if (api.ChatCommands.Get("chatformat") == null)
			{
				api.ChatCommands.Create("chatformat").WithDescription("Show your private chat formatting guide and available styles.").RequiresPlayer()
					.RequiresPrivilege(Privilege.chat)
					.HandleWith(OnChatFormatCommand);
			}
			else
			{
				api.Logger.Warning("[ServerEssentials/ChatDisplay] /chatformat is already registered; the existing command was preserved.");
			}
			api.Logger.Notification("[ServerEssentials/ChatDisplay] Ready. RolePrefix={0}, BoldName={1}, Timestamp={2}, General={3}, OtherGroups={4}, AdditionalGroups={5}.", config.ShowRoleBeforeName, config.BoldPlayerName, config.ShowTimestampBeforeMessage, config.FormatGeneralChat, config.FormatOtherChatGroups, string.Join(",", config.AdditionalFormattedChatGroupIds));
		}
	}

	private TextCommandResult OnChatFormatCommand(TextCommandCallingArgs args)
	{
		if (disposed)
		{
			return TextCommandResult.Error("Chat formatting is unavailable.");
		}
		if (!(args.Caller?.Player is IServerPlayer serverPlayer))
		{
			return TextCommandResult.Error("Run /chatformat in game.");
		}
		bool flag = config.MarkdownAllowedRoleCodes.Count == 0 || config.MarkdownAllowedRoleCodes.Contains(serverPlayer.Role?.Code ?? "", StringComparer.OrdinalIgnoreCase);
		string text;
		if (config.MarkdownEnabled)
		{
			text = ((!flag) ? "Formatting is not enabled for your role." : "Formatting is enabled for your role.");
		}
		else
		{
			text = "Formatting is disabled on this server.";
		}
		string text2;
		if (config.FormatOtherChatGroups)
		{
			text2 = "All other chat groups";
		}
		else
		{
			text2 = ((config.AdditionalFormattedChatGroupIds.Count > 0) ? ("Other group IDs: " + string.Join(", ", config.AdditionalFormattedChatGroupIds)) : "Other groups: off");
		}
		bool flag2 = config.MarkdownEnabled & flag;
		return TextCommandResult.Success("<strong>Chat formatting guide</strong>\n" + text + "\nGeneral chat: " + (config.FormatGeneralChat ? "on" : "off") + "; " + text2 + ".\nBold: <code>**text**</code> or <code>__text__</code>\nItalic: <code>*text*</code> or <code>_text_</code>\nBold + italic: <code>***text***</code>\nCode: <code>`literal text`</code>\nNested styles: <code>**bold with *italic***</code>\nLiteral symbols: <code>\\*text\\*</code> displays *text*.\nColors (" + ((flag2 && config.MarkdownAllowColors) ? "enabled" : "off for you") + "): <code>[color=#80C8FF]text[/color]</code>\nLinks (" + ((flag2 && config.MarkdownAllowLinks) ? "enabled" : "off for you") + "): <code>[label](https://example.com)</code>\nUse these in a normal chat message. Commands stay unchanged; raw HTML, underline and strikethrough are unsupported.");
	}

	public ChatDisplaySnapshot GetSnapshot()
	{
		return new ChatDisplaySnapshot
		{
			ShowRoleBeforeName = config.ShowRoleBeforeName,
			BoldPlayerName = config.BoldPlayerName,
			ShowTimestampBeforeMessage = config.ShowTimestampBeforeMessage,
			ChatFormat = config.ChatFormat,
			TimestampFormat = config.TimestampFormat,
			UseUtcTimestamp = config.UseUtcTimestamp,
			FormatGeneralChat = config.FormatGeneralChat,
			FormatOtherChatGroups = config.FormatOtherChatGroups,
			AdditionalFormattedChatGroupIds = config.AdditionalFormattedChatGroupIds.ToList(),
			MarkdownEnabled = config.MarkdownEnabled,
			MarkdownAllowColors = config.MarkdownAllowColors,
			MarkdownAllowLinks = config.MarkdownAllowLinks,
			MarkdownAllowedRoleCodes = config.MarkdownAllowedRoleCodes.ToList(),
			Preview = BuildPreview()
		};
	}

	public (bool Success, string Message, ChatDisplaySnapshot Snapshot) SaveSettings(ChatDisplaySettingsRequest? request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Chat display settings are required.", Snapshot: GetSnapshot());
		}
		ChatDisplayConfig chatDisplayConfig = new ChatDisplayConfig
		{
			SchemaVersion = 4,
			MarkdownEnabled = (request.MarkdownEnabled ?? config.MarkdownEnabled),
			MarkdownAllowColors = (request.MarkdownAllowColors ?? config.MarkdownAllowColors),
			MarkdownAllowLinks = (request.MarkdownAllowLinks ?? config.MarkdownAllowLinks),
			MarkdownAllowedRoleCodes = (request.MarkdownAllowedRoleCodes?.ToList() ?? config.MarkdownAllowedRoleCodes.ToList()),
			ShowRoleBeforeName = request.ShowRoleBeforeName,
			BoldPlayerName = request.BoldPlayerName,
			ShowTimestampBeforeMessage = request.ShowTimestampBeforeMessage,
			ChatFormat = (request.ChatFormat ?? config.ChatFormat),
			TimestampFormat = request.TimestampFormat,
			UseUtcTimestamp = request.UseUtcTimestamp,
			FormatGeneralChat = request.FormatGeneralChat,
			FormatOtherChatGroups = request.FormatOtherChatGroups,
			AdditionalFormattedChatGroupIds = (request.AdditionalFormattedChatGroupIds?.ToList() ?? new List<int>())
		};
		string text = (chatDisplayConfig.TimestampFormat ?? "").Trim();
		if (chatDisplayConfig.ShowTimestampBeforeMessage && string.IsNullOrWhiteSpace(text))
		{
			return (Success: false, Message: "Timestamp format cannot be empty while timestamps are enabled.", Snapshot: GetSnapshot());
		}
		try
		{
			DateTime.Now.ToString(string.IsNullOrWhiteSpace(text) ? "HH:mm" : text);
		}
		catch (Exception ex)
		{
			return (Success: false, Message: "Invalid timestamp format: " + ex.Message, Snapshot: GetSnapshot());
		}
		string text2 = ChatLayout.Validate(chatDisplayConfig.ChatFormat);
		if (text2 != null)
		{
			return (Success: false, Message: text2, Snapshot: GetSnapshot());
		}
		chatDisplayConfig.Clamp();
		config = chatDisplayConfig;
		moderationLines.Clear();
		SuiteConfigStorage.Store(api, config, "NewfiesChatDisplay.json");
		NotifyChanged();
		return (Success: true, Message: "Chat display settings saved and applied immediately.", Snapshot: GetSnapshot());
	}

	public string Reload()
	{
		config = ChatDisplayConfig.Load(api);
		moderationLines.Clear();
		NotifyChanged();
		return "Chat display configuration reloaded.";
	}

	private void NotifyChanged()
	{
		try
		{
			TreeAttribute treeAttribute = new TreeAttribute();
			treeAttribute.SetBool("showRoleBeforeName", config.ShowRoleBeforeName);
			treeAttribute.SetBool("boldPlayerName", config.BoldPlayerName);
			api.Event.PushEvent("newfiesse:chat-display-changed", treeAttribute);
		}
		catch
		{
		}
	}

	private void OnChatDisplayQuery(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (!disposed && data is ITreeAttribute treeAttribute)
		{
			treeAttribute.SetBool("available", value: true);
			treeAttribute.SetBool("showRoleBeforeName", config.ShowRoleBeforeName);
			treeAttribute.SetBool("boldPlayerName", config.BoldPlayerName);
			treeAttribute.SetBool("showTimestampBeforeMessage", config.ShowTimestampBeforeMessage);
			treeAttribute.SetString("chatFormat", config.ChatFormat);
			treeAttribute.SetString("timestampFormat", config.TimestampFormat ?? "HH:mm");
			treeAttribute.SetBool("useUtcTimestamp", config.UseUtcTimestamp);
			treeAttribute.SetBool("formatGeneralChat", config.FormatGeneralChat);
			treeAttribute.SetBool("formatOtherChatGroups", config.FormatOtherChatGroups);
			treeAttribute.SetString("additionalFormattedChatGroupIds", string.Join(",", config.AdditionalFormattedChatGroupIds));
		}
	}

	private void OnChatSenderQuery(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (disposed || !(data is ITreeAttribute treeAttribute))
		{
			return;
		}
		string uid = treeAttribute.GetString("playerUid", "");
		IPlayer player = api.World.AllOnlinePlayers.FirstOrDefault((IPlayer p) => p.PlayerUID == uid);
		if (player == null)
		{
			return;
		}
		string text = player.PlayerName ?? "";
		try
		{
			if (resolveDisplayName != null)
			{
				text = resolveDisplayName(uid, text) ?? text;
			}
		}
		catch
		{
			return;
		}
		treeAttribute.SetString("displayName", text);
		treeAttribute.SetBool("available", value: true);
	}

	private bool MarkdownAllowed(IServerPlayer player, int channelId)
	{
		return config.MarkdownEnabled && ShouldFormatChannel(channelId) && (config.MarkdownAllowedRoleCodes.Count == 0 || config.MarkdownAllowedRoleCodes.Contains(player.Role?.Code ?? "", StringComparer.OrdinalIgnoreCase));
	}

	private ChatMarkdown.Result PrepareMarkdown(IServerPlayer player, int channel, string body, bool allowAliases = true)
	{
		(string, int) key = (player.PlayerUID, channel);
		long ticks = DateTime.UtcNow.Ticks;
		if (moderationLines.TryGetValue(key, out ModerationLine value) && ticks - value.CreatedTicks <= TimeSpan.FromMilliseconds(100L).Ticks && (body == value.Raw || (allowAliases && (body == value.Result.Vtml || body == value.Result.ModerationText || body == value.EmittedBody || body == value.EmittedLine))))
		{
			return value.Result;
		}
		ChatMarkdown.Result result = ChatMarkdown.Format(body, config.MarkdownAllowColors, config.MarkdownAllowLinks);
		if (moderationLines.Count >= 1024)
		{
			moderationLines.Clear();
		}
		moderationLines[key] = new ModerationLine(body, result, ticks);
		return result;
	}

	private ChatMarkdown.Result? EmittedResult(IServerPlayer player, int channel, string body)
	{
		if (moderationLines.TryGetValue((player.PlayerUID ?? "", channel), out ModerationLine value) && DateTime.UtcNow.Ticks - value.CreatedTicks <= TimeSpan.FromMilliseconds(100L).Ticks && (body == value.EmittedBody || body == value.EmittedLine))
		{
			return value.Result;
		}
		return null;
	}

	private void OnModerationTextQuery(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (disposed || !(data is ITreeAttribute treeAttribute))
		{
			return;
		}
		string uid = treeAttribute.GetString("playerUid", "");
		IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID == uid);
		int num = treeAttribute.GetInt("channelId", -1);
		if (serverPlayer != null)
		{
			string text = treeAttribute.GetString("body", "");
			ChatMarkdown.Result result = EmittedResult(serverPlayer, num, treeAttribute.GetString("rawLine", text)) ?? EmittedResult(serverPlayer, num, text);
			if (result == null && MarkdownAllowed(serverPlayer, num))
			{
				result = PrepareMarkdown(serverPlayer, num, text);
			}
			if (!(result == null))
			{
				treeAttribute.SetString("moderationText", result.ModerationText);
				treeAttribute.SetBool("available", value: true);
			}
		}
	}

	private void OnPresentationQuery(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (disposed || !(data is ITreeAttribute treeAttribute))
		{
			return;
		}
		string uid = treeAttribute.GetString("playerUid", "");
		IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID == uid);
		if (serverPlayer != null)
		{
			int num = treeAttribute.GetInt("channelId", -1);
			string text = treeAttribute.GetString("body", "");
			if (!text.TrimStart().StartsWith('/'))
			{
				string text2 = (EmittedResult(serverPlayer, num, treeAttribute.GetString("rawLine", text)) ?? EmittedResult(serverPlayer, num, text))?.ModerationText ?? (MarkdownAllowed(serverPlayer, num) ? PrepareMarkdown(serverPlayer, num, text).ModerationText : text);
				ChatGuardModerationDecision chatGuardModerationDecision = ChatGuardControlBridge.Moderate(api, serverPlayer, num, text2);
				treeAttribute.SetString("visibleText", text2);
				treeAttribute.SetBool("blocked", chatGuardModerationDecision.Blocked);
				treeAttribute.SetBool("available", value: true);
			}
		}
	}

	private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
	{
		if (byPlayer == null || consumed.value || string.IsNullOrWhiteSpace(message))
		{
			return;
		}
		string text = message;
		try
		{
			string text2 = byPlayer.PlayerName ?? string.Empty;
			if (resolveDisplayName != null)
			{
				text2 = resolveDisplayName(byPlayer.PlayerUID ?? string.Empty, text2) ?? text2;
			}
			ChatMessageBoundary.Line line = ChatMessageBoundary.Parse(message, byPlayer.PlayerName, text2, byPlayer.Role?.Name);
			ParsedPlayerChatLine parsedPlayerChatLine = ExtractPlayerChatPresentation(message, byPlayer.PlayerName);
			if (line.HasSender)
			{
				parsedPlayerChatLine = new ParsedPlayerChatLine
				{
					Body = line.Body,
					PlayerNameColor = parsedPlayerChatLine.PlayerNameColor
				};
			}
			if (line.IsCommand)
			{
				return;
			}
			bool flag = MarkdownAllowed(byPlayer, channelId);
			ChatMarkdown.Result result = (flag ? PrepareMarkdown(byPlayer, channelId, parsedPlayerChatLine.Body, allowAliases: false) : null);
			ChatGuardModerationDecision chatGuardModerationDecision = ChatGuardControlBridge.Moderate(api, byPlayer, channelId, result?.ModerationText ?? parsedPlayerChatLine.Body);
			if (chatGuardModerationDecision.Available && chatGuardModerationDecision.Blocked)
			{
				consumed.value = true;
				return;
			}
			string text3 = resolveConfiguredNameColor?.Invoke(byPlayer.Role) ?? string.Empty;
			bool flag2 = !string.Equals(text2, byPlayer.PlayerName ?? string.Empty, StringComparison.Ordinal);
			bool flag3 = flag || config.ShowRoleBeforeName || config.ChatFormat != "{role} {name}: {timestamp} {message}" || config.BoldPlayerName || config.ShowTimestampBeforeMessage || !string.IsNullOrWhiteSpace(text3);
			if (!ShouldFormatChannel(channelId))
			{
				return;
			}
			if (!flag3)
			{
				if (flag2)
				{
					message = ReplaceSenderDisplayName(message, byPlayer.PlayerName, text2);
				}
				return;
			}
			string body = parsedPlayerChatLine.Body;
			string role = "";
			if (config.ShowRoleBeforeName)
			{
				IPlayerRole role2 = byPlayer.Role;
				string text4 = role2?.Name ?? role2?.Code ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(text4))
				{
					string value = ((role2 == null) ? "#FFFFFF" : $"#{role2.Color.R:X2}{role2.Color.G:X2}{role2.Color.B:X2}");
					string value2 = EscapeVtmlText(text4.Trim());
					role = $"<font color=\"{value}\"><strong>[{value2}]</strong></font>";
				}
			}
			string timestamp = "";
			if (config.ShowTimestampBeforeMessage)
			{
				DateTime dateTime = (config.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now);
				string text5;
				try
				{
					text5 = dateTime.ToString(config.TimestampFormat);
				}
				catch
				{
					text5 = dateTime.ToString("HH:mm");
				}
				timestamp = EscapeVtmlText("[" + text5 + "]");
			}
			string text6 = EscapeVtmlText(text2);
			string text7 = (config.BoldPlayerName ? ("<strong>" + text6 + "</strong>") : text6);
			string value3 = ((!string.IsNullOrWhiteSpace(text3)) ? text3 : parsedPlayerChatLine.PlayerNameColor);
			if (!string.IsNullOrWhiteSpace(value3))
			{
				text7 = $"<font color=\"{value3}\">{text7}</font>";
			}
			string text8 = result?.Vtml ?? EscapeVtmlText(body);
			string text9 = ChatLayout.Render(config.ChatFormat, role, text7, timestamp, text8, EscapeVtmlText);
			ChatMarkdown.Result result2 = result ?? new ChatMarkdown.Result(text8, body);
			if (moderationLines.Count >= 1024)
			{
				moderationLines.Clear();
			}
			moderationLines[(byPlayer.PlayerUID ?? "", channelId)] = new ModerationLine(body, result2, DateTime.UtcNow.Ticks)
			{
				EmittedBody = ChatMessageBoundary.Parse(text9, byPlayer.PlayerName, text2, byPlayer.Role?.Name).Body,
				EmittedLine = text9
			};
			message = text9;
		}
		catch (Exception ex)
		{
			message = text;
			long ticks = DateTime.UtcNow.Ticks;
			if (ticks - lastChatFailureTicks > TimeSpan.FromSeconds(30L).Ticks)
			{
				lastChatFailureTicks = ticks;
				api.Logger.Warning("[ChatDisplay] Formatting failed; original chat preserved. {0}: {1}", ex.GetType().Name, ex.Message);
			}
		}
	}

	private bool ShouldFormatChannel(int channelId)
	{
		if (channelId == GlobalConstants.GeneralChatGroup)
		{
			return config.FormatGeneralChat;
		}
		if (config.FormatOtherChatGroups)
		{
			return true;
		}
		return config.AdditionalFormattedChatGroupIds.Contains(channelId);
	}

	private static ParsedPlayerChatLine ExtractPlayerChatPresentation(string message, string? playerName)
	{
		if (string.IsNullOrEmpty(message))
		{
			return new ParsedPlayerChatLine();
		}
		string text = message.TrimStart();
		if (TryExtractColoredSenderPrefix(text, playerName, out string body, out string color))
		{
			return new ParsedPlayerChatLine
			{
				Body = body,
				PlayerNameColor = color
			};
		}
		if (text.StartsWith("<strong>", StringComparison.OrdinalIgnoreCase))
		{
			int num = text.IndexOf("</strong>", StringComparison.OrdinalIgnoreCase);
			if (num >= 0)
			{
				int length = "<strong>".Length;
				string senderWithColon = WebUtility.HtmlDecode(text.Substring(length, num - length)).Trim();
				if (SenderMatchesPlayer(senderWithColon, playerName))
				{
					int num2 = num + "</strong>".Length;
					return new ParsedPlayerChatLine
					{
						Body = ((num2 < text.Length) ? text.Substring(num2).TrimStart() : string.Empty)
					};
				}
			}
		}
		if (!string.IsNullOrWhiteSpace(playerName))
		{
			string text2 = playerName.Trim() + ":";
			if (text.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
			{
				return new ParsedPlayerChatLine
				{
					Body = text.Substring(text2.Length).TrimStart()
				};
			}
		}
		return new ParsedPlayerChatLine
		{
			Body = text
		};
	}

	private static string ReplaceSenderDisplayName(string message, string? realName, string? displayName)
	{
		if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(realName) || string.IsNullOrWhiteSpace(displayName))
		{
			return message;
		}
		string safeDisplayName = EscapeVtmlText(displayName.Trim());
		int num = message.Length - message.TrimStart().Length;
		string text = ((num > 0) ? message.Substring(0, num) : string.Empty);
		string text2 = message.Substring(num);
		if (text2.StartsWith("<font", StringComparison.OrdinalIgnoreCase))
		{
			int num2 = text2.IndexOf('>');
			if (num2 >= 0)
			{
				int num3 = text2.IndexOf("</font>", num2 + 1, StringComparison.OrdinalIgnoreCase);
				if (num3 >= 0)
				{
					int num4 = num2 + 1;
					string text3 = text2.Substring(num4, num3 - num4);
					string text4 = ReplaceStrongOrPlainSender(text3, realName, safeDisplayName);
					if ((object)text4 != text3 && !string.Equals(text4, text3, StringComparison.Ordinal))
					{
						return text + text2.Substring(0, num2 + 1) + text4 + text2.Substring(num3);
					}
				}
			}
		}
		string text5 = ReplaceStrongOrPlainSender(text2, realName, safeDisplayName);
		if (!string.Equals(text5, text2, StringComparison.Ordinal))
		{
			return text + text5;
		}
		return message;
	}

	private static string ReplaceStrongOrPlainSender(string value, string realName, string safeDisplayName)
	{
		if (value.StartsWith("<strong>", StringComparison.OrdinalIgnoreCase))
		{
			int num = value.IndexOf("</strong>", StringComparison.OrdinalIgnoreCase);
			if (num >= 0)
			{
				int length = "<strong>".Length;
				string text = WebUtility.HtmlDecode(value.Substring(length, num - length)).Trim();
				if (SenderMatchesPlayer(text, realName))
				{
					string text2 = (text.EndsWith(':') ? ":" : string.Empty);
					return "<strong>" + safeDisplayName + text2 + "</strong>" + value.Substring(num + "</strong>".Length);
				}
			}
		}
		string text3 = realName.Trim() + ":";
		if (value.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
		{
			return safeDisplayName + ":" + value.Substring(text3.Length);
		}
		return value;
	}

	private static bool TryExtractColoredSenderPrefix(string value, string? playerName, out string body, out string color)
	{
		body = string.Empty;
		color = string.Empty;
		if (!value.StartsWith("<font", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		int num = value.IndexOf('>');
		if (num < 0)
		{
			return false;
		}
		string fontTag = value.Substring(0, num + 1);
		if (!TryReadSafeHexColor(fontTag, out color))
		{
			return false;
		}
		int num2 = value.IndexOf("</font>", num + 1, StringComparison.OrdinalIgnoreCase);
		if (num2 < 0)
		{
			return false;
		}
		int num3 = num + 1;
		string text = value.Substring(num3, num2 - num3).Trim();
		if (text.StartsWith("<strong>", StringComparison.OrdinalIgnoreCase))
		{
			int num4 = text.IndexOf("</strong>", StringComparison.OrdinalIgnoreCase);
			if (num4 < 0)
			{
				return false;
			}
			num3 = "<strong>".Length;
			string senderWithColon = WebUtility.HtmlDecode(text.Substring(num3, num4 - num3)).Trim();
			if (!SenderMatchesPlayer(senderWithColon, playerName))
			{
				return false;
			}
			if (!string.IsNullOrWhiteSpace(text.Substring(num4 + "</strong>".Length)))
			{
				return false;
			}
		}
		else
		{
			string senderWithColon2 = WebUtility.HtmlDecode(text).Trim();
			if (!SenderMatchesPlayer(senderWithColon2, playerName))
			{
				return false;
			}
		}
		int num5 = num2 + "</font>".Length;
		body = ((num5 < value.Length) ? value.Substring(num5).TrimStart() : string.Empty);
		return true;
	}

	private static bool SenderMatchesPlayer(string senderWithColon, string? playerName)
	{
		if (string.IsNullOrWhiteSpace(playerName))
		{
			return false;
		}
		string text = senderWithColon.Trim();
		if (text.EndsWith(':'))
		{
			string text2 = text;
			text = text2.Substring(0, text2.Length - 1).TrimEnd();
		}
		return text.Equals(playerName.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryReadSafeHexColor(string fontTag, out string color)
	{
		color = string.Empty;
		int num = fontTag.IndexOf("color", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			return false;
		}
		int num2 = fontTag.IndexOf('=', num + 5);
		if (num2 < 0)
		{
			return false;
		}
		int i;
		for (i = num2 + 1; i < fontTag.Length && char.IsWhiteSpace(fontTag[i]); i++)
		{
		}
		if (i >= fontTag.Length)
		{
			return false;
		}
		char c = fontTag[i];
		if (c != '"' && c != '\'')
		{
			return false;
		}
		int num3 = fontTag.IndexOf(c, i + 1);
		if (num3 < 0)
		{
			return false;
		}
		int num4 = i + 1;
		string text = fontTag.Substring(num4, num3 - num4).Trim();
		if (text.Length != 7 || text[0] != '#')
		{
			return false;
		}
		for (int j = 1; j < text.Length; j++)
		{
			if (!Uri.IsHexDigit(text[j]))
			{
				return false;
			}
		}
		color = text.ToUpperInvariant();
		return true;
	}

	private static string EscapeVtmlText(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		return text.Replace("<", "&lt;").Replace(">", "&gt;");
	}

	private string BuildPreview()
	{
		string role = (config.ShowRoleBeforeName ? "[Player]" : "");
		string timestamp = (config.ShowTimestampBeforeMessage ? ("[" + (config.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now).ToString(config.TimestampFormat) + "]") : "");
		return ChatLayout.Render(config.ChatFormat, role, "ExamplePlayer", timestamp, "Hello world", (string value) => value);
	}

	public void Dispose()
	{
		disposed = true;
		if (registered)
		{
			registered = false;
			api.Event.PlayerChat -= OnPlayerChat;
		}
	}
}
