using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesCommunityMOTD;

public class MotdServerSystem : ModSystem
{
	private ICoreServerAPI? sapi;

	private MotdConfig? cfg;

	private readonly CachedLogoFile logoFile = new CachedLogoFile();

	private string logoFileHash = "";

	private EventBusListenerDelegate? controlListener;

	private readonly DateTime serverStartedUtc = DateTime.UtcNow;

	public const string ControlEvent = "newfiescommunitymotd:control";

	public const string PublicVersion = "2.1.6";

	private const string ConfigFileName = "newfiescommunitymotd.json";

	private const string ChannelName = "newfiescommunitymotd";

	private const int MIN_W = 700;

	private const int MAX_W = 1600;

	private const int MIN_H = 520;

	private const int MAX_H = 1000;

	public override bool ShouldLoad(EnumAppSide forSide)
	{
		return forSide == EnumAppSide.Server || forSide == EnumAppSide.Universal;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;
		LoadConfig();
		NewfiesMotdIntegration.RegisterDiscord(new DiscordRuntimeAdapter());
		sapi.Network.RegisterChannel("newfiescommunitymotd").RegisterMessageType(typeof(MotdPacket)).RegisterMessageType(typeof(MotdResetPacket))
			.SetMessageHandler<MotdPacket>(OnClientRequest);
		sapi.Event.PlayerNowPlaying += PlayerNowPlaying;
		sapi.ChatCommands.Create("motd").WithDescription("Open the Message of the Day.").RequiresPrivilege(Privilege.chat)
			.RequiresPlayer()
			.HandleWith((TextCommandCallingArgs args) =>
			{
				if (args.Caller?.Player is IServerPlayer player)
				{
					SendMotd(player, forceShow: true);
					return TextCommandResult.Success();
				}
				return TextCommandResult.Error("Player only.");
			});
		sapi.ChatCommands.Create("motdreload").WithDescription("Reload MOTD config from disk.").RequiresPrivilege(Privilege.controlserver)
			.HandleWith((TextCommandCallingArgs args) =>
			{
				LoadConfig();
				return TextCommandResult.Success("[MOTD] Config reloaded.");
			});
		sapi.ChatCommands.Create("motdresetall").WithDescription("Clear all client seen-MOTD caches.").RequiresPrivilege(Privilege.controlserver)
			.HandleWith((TextCommandCallingArgs args) =>
			{
				BroadcastSeenReset();
				return TextCommandResult.Success("[MOTD] Reset broadcast sent.");
			});
		controlListener = OnSuiteControl;
		sapi.Event.RegisterEventBusListener(controlListener, 0.5, "newfiescommunitymotd:control");
		sapi.Logger.Notification("[MOTD] Server started. v{0}; Suite companion control available.", "2.1.6");
	}

	private void OnSuiteControl(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (sapi == null || !(data is ITreeAttribute treeAttribute))
		{
			return;
		}
		treeAttribute.SetBool("available", value: true);
		treeAttribute.SetString("version", "2.1.6");
		string text = (treeAttribute.GetString("operation", "get") ?? "get").Trim().ToLowerInvariant();
		try
		{
			switch (text)
			{
			case "get":
			case "status":
				treeAttribute.SetBool("success", value: true);
				treeAttribute.SetString("message", "Community MOTD companion is connected.");
				treeAttribute.SetString("configJson", JsonConvert.SerializeObject(cfg ?? CreateFreshDefaults(), Formatting.None));
				break;
			case "apply":
			case "save":
			{
				string value = treeAttribute.GetString("configJson", "") ?? "";
				MotdConfig motdConfig = JsonConvert.DeserializeObject<MotdConfig>(value);
				if (motdConfig == null)
				{
					throw new InvalidOperationException("MOTD configuration payload was empty.");
				}
				NormalizeConfig(motdConfig);
				cfg = motdConfig;
				sapi.StoreModConfig(cfg, "newfiescommunitymotd.json");
				treeAttribute.SetBool("success", value: true);
				treeAttribute.SetString("message", "Community MOTD configuration saved. Players receive it on the next MOTD open.");
				treeAttribute.SetString("configJson", JsonConvert.SerializeObject(cfg, Formatting.None));
				break;
			}
			case "reload":
				LoadConfig();
				treeAttribute.SetBool("success", value: true);
				treeAttribute.SetString("message", "Community MOTD configuration reloaded from disk. Players receive it on the next MOTD open.");
				treeAttribute.SetString("configJson", JsonConvert.SerializeObject(cfg ?? CreateFreshDefaults(), Formatting.None));
				break;
			case "resetseen":
			case "reset-seen":
				BroadcastSeenReset();
				treeAttribute.SetBool("success", value: true);
				treeAttribute.SetString("message", "Community MOTD seen-state reset broadcast sent.");
				treeAttribute.SetString("configJson", JsonConvert.SerializeObject(cfg ?? CreateFreshDefaults(), Formatting.None));
				break;
			default:
				treeAttribute.SetBool("success", value: false);
				treeAttribute.SetString("message", "Unknown Community MOTD control operation: " + text);
				break;
			}
		}
		catch (Exception ex)
		{
			treeAttribute.SetBool("success", value: false);
			treeAttribute.SetString("message", ex.Message);
			sapi.Logger.Warning("[MOTD] Suite companion control '{0}' failed: {1}: {2}", text, ex.GetType().Name, ex.Message);
		}
		handling = EnumHandling.PreventSubsequent;
	}

	private byte[] GetServerLogoBytes()
	{
		logoFileHash = "";
		if (sapi == null || cfg == null || cfg.EnableLogo != true || !string.Equals(cfg.LogoSource, "ServerFile", StringComparison.OrdinalIgnoreCase))
		{
			return Array.Empty<byte>();
		}
		try
		{
			string orCreateDataPath = sapi.GetOrCreateDataPath("NewfiesCommunityMOTD");
			string path = Path.Combine(orCreateDataPath, "branding");
			Directory.CreateDirectory(path);
			string text = (string.IsNullOrWhiteSpace(cfg.ServerLogoFile) ? "branding/motd-logo.png" : cfg.ServerLogoFile.Trim().Replace('\\', '/'));
			while (text.StartsWith("/", StringComparison.Ordinal))
			{
				text = text.Substring(1);
			}
			string path2 = text.Replace('/', Path.DirectorySeparatorChar);
			string text2 = Path.GetFullPath(Path.Combine(orCreateDataPath, path2));
			string fullPath = Path.GetFullPath(orCreateDataPath + Path.DirectorySeparatorChar);
			if (!text2.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
			{
				return Array.Empty<byte>();
			}
			if (!File.Exists(text2) && string.Equals(text, "branding/motd-logo.png", StringComparison.OrdinalIgnoreCase))
			{
				string text3 = Path.Combine(orCreateDataPath, "motd-logo.png");
				if (File.Exists(text3))
				{
					text2 = text3;
				}
			}
			if (!File.Exists(text2))
			{
				return Array.Empty<byte>();
			}
			(byte[], string) tuple = logoFile.Read(text2);
			logoFileHash = tuple.Item2;
			return tuple.Item1;
		}
		catch (Exception ex)
		{
			sapi.Logger.Warning("[MOTD] Could not load server logo: {0}", ex.Message);
			return Array.Empty<byte>();
		}
	}

	private void BroadcastSeenReset()
	{
		sapi?.Network.GetChannel("newfiescommunitymotd")?.BroadcastPacket(new MotdResetPacket
		{
			ResetAll = true
		});
	}

	private void TryUnregisterControlListener()
	{
		if (sapi != null && controlListener != null)
		{
			try
			{
				sapi.Event.GetType().GetMethod("UnregisterEventBusListener", new Type[1] { typeof(EventBusListenerDelegate) })?.Invoke(sapi.Event, new object[1] { controlListener });
			}
			catch
			{
			}
			controlListener = null;
		}
	}

	public override void Dispose()
	{
		if (sapi != null)
		{
			try
			{
				sapi.Event.PlayerNowPlaying -= PlayerNowPlaying;
			}
			catch
			{
			}
			TryUnregisterControlListener();
		}
		sapi = null;
		base.Dispose();
	}

	private void PlayerNowPlaying(IServerPlayer player)
	{
		SendMotd(player, forceShow: false);
	}

	private void OnClientRequest(IServerPlayer player, MotdPacket pkt)
	{
		SendMotd(player, forceShow: true);
	}

	private void SendMotd(IServerPlayer player, bool forceShow)
	{
		if (cfg != null && sapi != null)
		{
			var (text, text2) = BuildVtml(cfg, player);
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = "<strong>MOTD</strong>\n(Empty — edit ModConfig/newfiescommunitymotd.json)";
			}
			int width = GameMath.Clamp(cfg.Width.Value, 700, 1600);
			int height = GameMath.Clamp(cfg.Height.Value, 520, 1000);
			IReadOnlyList<MotdFeedItem> remote = NewfiesMotdIntegration.Discord?.GetAnnouncements();
			IReadOnlyList<MotdFeedItem> remote2 = NewfiesMotdIntegration.Discord?.GetUpdates();
			byte[] serverLogoBytes = GetServerLogoBytes();
			MotdPacket message = new MotdPacket
			{
				Title = (string.IsNullOrWhiteSpace(text) ? "Message of the Day" : text),
				ContentVtml = text2,
				Width = width,
				Height = height,
				ScrollBarWidth = GameMath.Clamp(cfg.ScrollBarWidth.Value, 10, 30),
				ScrollEstimateMultiplier = Math.Max(0.5f, cfg.ScrollEstimateMultiplier.Value),
				ScrollExtraPixels = GameMath.Clamp(cfg.ScrollExtraPixels.Value, -200, 200),
				ScrollOverrideHeightPx = Math.Max(0, cfg.ScrollOverrideHeightPx.Value),
				OpenOnEveryJoin = cfg.OpenOnEveryJoin.Value,
				OnlyOncePerSession = cfg.OnlyOncePerSession.Value,
				OnlyOncePerVersion = cfg.OnlyOncePerVersion.Value,
				VersionLabel = (cfg.VersionLabel ?? ""),
				ContentHash = Sha256Hex(text2),
				ForceShow = forceShow,
				IsRefresh = false,
				EnableDontShowAgain = (cfg.EnableDontShowAgain ?? true),
				EnableLinkButton = (cfg.EnableLinkButton == true),
				LinkButtonText = (cfg.LinkButtonText ?? "Open Link"),
				LinkUrl = (cfg.LinkUrl ?? ""),
				EnableWebMapButton = (cfg.EnableWebMapButton == true),
				WebMapButtonText = (cfg.WebMapButtonText ?? "Web Map"),
				WebMapUrl = (cfg.WebMapUrl ?? ""),
				Subtitle = (cfg.Subtitle ?? ""),
				AccentColor = (cfg.AccentColor ?? "#FFC857"),
				EnableLogo = (cfg.EnableLogo == true),
				LogoAsset = (cfg.LogoAsset ?? ""),
				LogoSource = (cfg.LogoSource ?? "Asset"),
				ServerLogoHash = logoFileHash,
				ServerLogoPng = serverLogoBytes,
				LogoWidth = GameMath.Clamp(cfg.LogoWidth ?? 180, 0, 400),
				LogoHeight = GameMath.Clamp(cfg.LogoHeight.GetValueOrDefault(), 0, 400),
				LogoPreserveAspectRatio = (cfg.LogoPreserveAspectRatio ?? true),
				EnablePlayersTab = (cfg.EnablePlayersTab ?? true),
				ShowPlayerRoles = (cfg.ShowPlayerRoles ?? true),
				ShowPlayerPing = (cfg.ShowPlayerPing ?? true),
				SeparateStaff = (cfg.SeparateStaff ?? true),
				OnlineCount = sapi.World.AllOnlinePlayers.Count((IPlayer p) => p != null),
				MaxPlayers = sapi.Server.Config.MaxClients,
				InGameDate = BuildInGameDate(),
				InGameTime = BuildInGameTime(),
				Season = BuildSeason(),
				Players = BuildPlayerList(),
				FooterText = (cfg.FooterText ?? ""),
				EnableAnnouncementsTab = (cfg.EnableAnnouncementsTab ?? true),
				EnableUpdatesTab = (cfg.EnableUpdatesTab ?? true),
				EnableEventsTab = (cfg.EnableEventsTab ?? true),
				EnableRulesTab = (cfg.EnableRulesTab ?? true),
				EnableServerTab = (cfg.EnableServerTab ?? true),
				EnableCommandsTab = (cfg.EnableCommandsTab ?? true),
				EnableCommunityTab = false,
				AnnouncementsVtml = BuildFeed("Announcements", cfg.AnnouncementsVtml, cfg.UseDiscordAnnouncements ?? true, remote),
				UpdatesVtml = BuildFeed("Updates", cfg.UpdatesVtml, cfg.UseDiscordUpdates ?? true, remote2),
				AnnouncementIds = FeedIds(remote, cfg.UseDiscordAnnouncements ?? true),
				UpdateIds = FeedIds(remote2, cfg.UseDiscordUpdates ?? true),
				EventsVtml = BuildFeed("Events", cfg.EventsVtml, cfg.UseDiscordEvents ?? true, NewfiesMotdIntegration.Discord?.GetEvents()),
				RulesVtml = (cfg.RulesVtml ?? ""),
				ServerVtml = BuildServerVtml(cfg.ServerInfoVtml),
				ServerInfoLeftVtml = BuildServerInfoLeftVtml(cfg.ServerInfoVtml),
				ServerInfoRightVtml = BuildServerInfoRightVtml(),
				CommandsVtml = (cfg.CommandsVtml ?? ""),
				CommunityVtml = (cfg.CommunityVtml ?? ""),
				DiscordConnected = (NewfiesMotdIntegration.Discord?.IsAvailable ?? false),
				SuiteConnected = (NewfiesMotdIntegration.Suite?.IsAvailable ?? false),
				IntegrationProtocol = 1
			};
			sapi.Logger.Debug("[MOTD] Sending on open to {0}: len={1}, force={2}", player.PlayerName, text2.Length, forceShow);
			sapi.Network.GetChannel("newfiescommunitymotd").SendPacket(message, player);
		}
	}

	private string BuildInGameDate()
	{
		IGameCalendar calendar = sapi.World.Calendar;
		if (calendar == null)
		{
			return "Unavailable";
		}
		string text = calendar.PrettyDate();
		int num = text.LastIndexOf(',');
		if (num > 0)
		{
			string text2 = text.Substring(num + 1);
			if (text2.Contains(":"))
			{
				text = text.Substring(0, num);
			}
		}
		return text.Trim();
	}

	private string BuildInGameTime()
	{
		IGameCalendar calendar = sapi.World.Calendar;
		if (calendar == null)
		{
			return "";
		}
		try
		{
			double num = calendar.FullHourOfDay;
			int value = ((int)Math.Floor(num) % 24 + 24) % 24;
			int value2 = Math.Clamp((int)Math.Floor((num - Math.Floor(num)) * 60.0), 0, 59);
			return $"{value:00}:{value2:00}";
		}
		catch
		{
			return "";
		}
	}

	private string BuildSeason()
	{
		IGameCalendar calendar = sapi.World.Calendar;
		if (calendar == null)
		{
			return "";
		}
		try
		{
			int month = calendar.Month;
			int num = 12;
			try
			{
				if (calendar.GetType().GetProperty("MonthsPerYear")?.GetValue(calendar) is int num2 && num2 >= 4)
				{
					num = num2;
				}
			}
			catch
			{
			}
			int num3 = (month % num + num) % num;
			int num4 = Math.Min(3, num3 * 4 / num);
			if (1 == 0)
			{
			}
			string result = num4 switch
			{
				0 => "Spring", 
				1 => "Summer", 
				2 => "Autumn", 
				_ => "Winter", 
			};
			if (1 == 0)
			{
			}
			return result;
		}
		catch
		{
			return "";
		}
	}

	private List<string> FeedIds(IReadOnlyList<MotdFeedItem>? remote, bool enabled)
	{
		if (!enabled || remote == null)
		{
			return new List<string>();
		}
		int count = GameMath.Clamp(cfg?.DiscordMaxItemsPerFeed ?? 10, 1, 50);
		return (from x in remote.Take(count)
			select string.IsNullOrWhiteSpace(x.Id) ? $"{x.UnixTime}:{x.Title}" : x.Id into x
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct(StringComparer.Ordinal).ToList();
	}

	private string BuildFeed(string heading, string? localVtml, bool useDiscord, IReadOnlyList<MotdFeedItem>? remote)
    {
        return MotdFeedFormatter.Build(heading, localVtml, useDiscord, remote,
            cfg?.DiscordMaxItemsPerFeed ?? 10, cfg?.AccentColor,
            cfg?.MergeLocalAndDiscord ?? true);
    }

	private string BuildServerVtml(string? customVtml)
	{
		return BuildServerInfoLeftVtml(customVtml) + "\n\n" + BuildServerInfoRightVtml();
	}

	private string BuildServerInfoLeftVtml(string? customVtml)
	{
		if (sapi == null)
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder();
		string accent = cfg?.AccentColor ?? "#FFC857";
		Section(stringBuilder, "SERVER", accent);
		if (!string.IsNullOrWhiteSpace(customVtml))
		{
			stringBuilder.Append(customVtml).Append("\n\n");
		}
		MotdConfig? motdConfig = cfg;
		if (motdConfig == null || motdConfig.ServerInfoShowServer != false)
		{
			AddInfo(stringBuilder, "Server name", sapi.Server.Config.ServerName);
			AddInfo(stringBuilder, "Players", $"{sapi.World.AllOnlinePlayers.Count((IPlayer p) => p != null)} / {sapi.Server.Config.MaxClients}");
			AddInfo(stringBuilder, "Uptime", FormatDuration(DateTime.UtcNow - serverStartedUtc));
			AddInfo(stringBuilder, "Game version", GetStaticVersion());
			AddInfo(stringBuilder, "Community MOTD", "2.1.6");
			AddReflectedProperty(stringBuilder, "Port", sapi.Server.Config, "Port");
			AddReflectedProperty(stringBuilder, "Connection timeout", sapi.Server.Config, "ConnectionTimeout");
			AddReflectedProperty(stringBuilder, "Public listing", sapi.Server.Config, "AdvertiseServer");
			AddReflectedProperty(stringBuilder, "Server language", sapi.Server.Config, "ServerLanguage");
			AddReflectedProperty(stringBuilder, "Max clients", sapi.Server.Config, "MaxClients");
		}
		MotdConfig? motdConfig2 = cfg;
		if (motdConfig2 == null || motdConfig2.ServerInfoShowGameplay != false)
		{
			Section(stringBuilder, "GAMEPLAY", "#8FCF8A");
			AddWorldSetting(stringBuilder, "Game mode", "gameMode", "gamemode");
			AddWorldSetting(stringBuilder, "Player health", "playerHealthPoints", "playerHealth");
			AddWorldSetting(stringBuilder, "Hunger speed", "hungerSpeed", "hungerSpeedModifier");
			AddWorldSetting(stringBuilder, "Creature strength", "creatureStrength", "creatureStrengthModifier");
			AddWorldSetting(stringBuilder, "Creature hostility", "creatureHostility");
			AddWorldSetting(stringBuilder, "Death punishment", "deathPunishment");
			AddWorldSetting(stringBuilder, "PvP", "allowPvP", "pvp");
			AddWorldSetting(stringBuilder, "Entity spawning", "entitySpawning", "allowEntitySpawning");
			AddWorldSetting(stringBuilder, "Class selection", "classExclusiveRecipes", "allowClassSelection");
			AddWorldSetting(stringBuilder, "Land claiming", "allowLandClaiming", "landClaiming");
			AddWorldSetting(stringBuilder, "Chiseling", "allowChiseling", "microblockChiseling");
			AddWorldSetting(stringBuilder, "Fire spread", "allowFireSpread", "fireSpread");
			AddWorldSetting(stringBuilder, "Block gravity", "blockGravity", "allowBlockGravity");
		}
		MotdConfig? motdConfig3 = cfg;
		if (motdConfig3 == null || motdConfig3.ServerInfoShowTemporal != false)
		{
			Section(stringBuilder, "TEMPORAL", "#B69BE8");
			AddWorldSetting(stringBuilder, "Temporal stability", "temporalStability", "temporalStabilityEnabled");
			AddWorldSetting(stringBuilder, "Temporal storms", "temporalStorms", "temporalStormsEnabled");
			AddWorldSetting(stringBuilder, "Storm frequency", "temporalStormFrequency");
			AddWorldSetting(stringBuilder, "Temporal gear", "temporalGearRespawnUses", "temporalGear");
		}
		MotdConfig? motdConfig4 = cfg;
		if (motdConfig4 == null || motdConfig4.UseSuiteStatus != false)
		{
			string suiteStatusVtml = GetSuiteStatusVtml();
			if (!string.IsNullOrWhiteSpace(suiteStatusVtml))
			{
				Section(stringBuilder, "SUITE", "#7FA7C0");
				stringBuilder.Append(suiteStatusVtml);
			}
		}
		return stringBuilder.ToString();
	}

	private string BuildServerInfoRightVtml()
	{
		if (sapi == null)
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder();
		string text = cfg?.AccentColor ?? "#FFC857";
		Section(stringBuilder, "WORLD", "#E2C46E");
		MotdConfig? motdConfig = cfg;
		if (motdConfig == null || motdConfig.ServerInfoShowWorld != false)
		{
			AddInfo(stringBuilder, "In-game date", BuildInGameDate());
			AddInfo(stringBuilder, "In-game time", BuildInGameTime());
			AddInfo(stringBuilder, "Season", BuildSeason());
			try
			{
				ISaveGame saveGame = sapi.WorldManager?.SaveGame;
				if (saveGame != null)
				{
					AddInfo(stringBuilder, "Play style", saveGame.PlayStyle);
					AddInfo(stringBuilder, "World type", saveGame.WorldType);
					AddInfo(stringBuilder, "Entity spawning", saveGame.EntitySpawning ? "Enabled" : "Disabled");
					AddInfo(stringBuilder, "Last saved game version", saveGame.LastSavedGameVersion);
				}
				if (sapi.WorldManager != null)
				{
					AddInfo(stringBuilder, "Chunk size", $"{sapi.WorldManager.ChunkSize} blocks");
				}
			}
			catch
			{
			}
			IGameCalendar calendar = sapi.World.Calendar;
			if (calendar != null)
			{
				AddInfo(stringBuilder, "Days per month", calendar.DaysPerMonth.ToString());
				AddReflectedProperty(stringBuilder, "Hours per day", calendar, "HoursPerDay");
				AddReflectedProperty(stringBuilder, "Calendar speed", calendar, "SpeedOfTime");
			}
			AddMapSize(stringBuilder);
			AddWorldSetting(stringBuilder, "World height", "worldHeight");
			AddWorldSetting(stringBuilder, "Sea level", "seaLevel");
			AddWorldSetting(stringBuilder, "Landcover", "landcover");
			AddWorldSetting(stringBuilder, "Upheaval", "upheavalCommonness", "upheaval");
			AddWorldSetting(stringBuilder, "Geologic activity", "geoActivity", "geologicActivity");
			AddWorldSetting(stringBuilder, "Polar-equator distance", "polarEquatorDistance");
			AddWorldSetting(stringBuilder, "Starting climate", "startingClimate", "startingClimateMode");
			AddWorldSetting(stringBuilder, "Starting temperature", "startingTemperature");
			AddWorldSetting(stringBuilder, "Starting rainfall", "startingRainfall");
		}
		MotdConfig? motdConfig2 = cfg;
		if (motdConfig2 == null || motdConfig2.ServerInfoShowProspecting != false)
		{
			Section(stringBuilder, "PROSPECTING & RESOURCES", "#78C7C9");
			AddWorldSetting(stringBuilder, "Propick node radius", "propickNodeSearchRadius", "propickNodeSearchRange", "propickRange");
			AddWorldSetting(stringBuilder, "Propick node mode", "propickNodeSearch", "propickNodeSearchEnabled");
			AddWorldSetting(stringBuilder, "Global deposit rate", "globalDepositSpawnRate", "depositSpawnRate");
			AddWorldSetting(stringBuilder, "Surface copper", "surfaceCopperDeposits", "surfaceCopper");
			AddWorldSetting(stringBuilder, "Surface tin", "surfaceTinDeposits", "surfaceTin");
			AddWorldSetting(stringBuilder, "Ore deposits", "oreDepositRate", "oreDeposits");
			AddWorldSetting(stringBuilder, "Tree growth speed", "treeGrowthSpeed", "saplingGrowthRate");
			AddWorldSetting(stringBuilder, "Food spoilage speed", "foodSpoilageSpeed", "foodSpoilageRate");
			AddWorldSetting(stringBuilder, "Tool durability", "toolDurability", "toolDurabilityModifier");
		}
		MotdConfig? motdConfig3 = cfg;
		if (motdConfig3 == null || motdConfig3.ServerInfoShowMods != false)
		{
			List<Mod> list = (from g in sapi.ModLoader.Mods.Where((Mod m) => m?.Info != null && !m.Info.CoreMod && !string.Equals(m.Info.ModID, "game", StringComparison.OrdinalIgnoreCase)).GroupBy((Mod m) => m.Info.ModID ?? "", StringComparer.OrdinalIgnoreCase)
				select g.First()).OrderBy((Mod m) => string.IsNullOrWhiteSpace(m.Info.Name) ? m.Info.ModID : m.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
			Section(stringBuilder, $"INSTALLED MODS - {list.Count}", "#9FB4C8");
			int num = 0;
			foreach (Mod item in list)
			{
				num++;
				string value = (string.IsNullOrWhiteSpace(item.Info.Name) ? (item.Info.ModID ?? "Unknown") : item.Info.Name);
				string value2 = CompactModName(value, 38);
				string value3 = (string.IsNullOrWhiteSpace(item.Info.Version) ? "" : item.Info.Version);
				string value4 = ((num % 2 == 0) ? "#D9E2EE" : "#F2F4F8");
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder3 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder2);
				handler.AppendLiteral("<font color=\"#6F8195\">");
				handler.AppendFormatted(num, "00");
				handler.AppendLiteral("</font>  ");
				stringBuilder3.Append(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(39, 2, stringBuilder2);
				handler.AppendLiteral("<font color=\"");
				handler.AppendFormatted(value4);
				handler.AppendLiteral("\"><strong>");
				handler.AppendFormatted(WebEncode(value2));
				handler.AppendLiteral("</strong></font>");
				stringBuilder4.Append(ref handler);
				if (!string.IsNullOrWhiteSpace(value3))
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder5 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
					handler.AppendLiteral("  <font color=\"#7FA7C0\">v");
					handler.AppendFormatted(WebEncode(value3));
					handler.AppendLiteral("</font>");
					stringBuilder5.Append(ref handler);
				}
				stringBuilder.Append("\n");
			}
			if (list.Count == 0)
			{
				stringBuilder.Append("<font color=\"#7FA7C0\"><i>No additional mods are loaded.</i></font>\n");
			}
		}
		return stringBuilder.ToString();
	}

	private string GetSuiteStatusVtml()
	{
		if (sapi == null)
		{
			return "";
		}
		string text = "";
		try
		{
			TreeAttribute treeAttribute = new TreeAttribute();
			treeAttribute.SetBool("available", value: false);
			treeAttribute.SetInt("protocol", 1);
			treeAttribute.SetString("vtml", "");
			sapi.Event.PushEvent("newfiesserveressentials:motd-status", treeAttribute);
			if (treeAttribute.GetBool("available") && treeAttribute.GetInt("protocol") == 1)
			{
				text = treeAttribute.GetString("vtml", "") ?? "";
			}
		}
		catch
		{
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			INewfiesSuiteMotdProvider? suite = NewfiesMotdIntegration.Suite;
			if (suite != null && suite.IsAvailable)
			{
				text = NewfiesMotdIntegration.Suite.GetPlayerFacingStatusVtml();
			}
		}
		return text;
	}

	private static void Section(StringBuilder sb, string title, string accent)
	{
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(55, 2, sb);
		handler.AppendLiteral("\n<font size=\"14\" color=\"");
		handler.AppendFormatted(accent);
		handler.AppendLiteral("\"><strong>[ ");
		handler.AppendFormatted(WebEncode(title));
		handler.AppendLiteral(" ]</strong></font>\n");
		sb.Append(ref handler);
	}

	private static void AddInfo(StringBuilder sb, string label, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			string text = value.Trim();
			string value2 = WebEncode(label);
			string value3 = WebEncode(text);
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder);
			handler.AppendLiteral("<font color=\"#9EADBD\">");
			handler.AppendFormatted(value2);
			handler.AppendLiteral("</font>  ");
			stringBuilder2.Append(ref handler);
			if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "On", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "True", StringComparison.OrdinalIgnoreCase))
			{
				sb.Append("<font color=\"#83C98C\"><strong>[ON]</strong></font>\n");
				return;
			}
			if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Off", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "False", StringComparison.OrdinalIgnoreCase))
			{
				sb.Append("<font color=\"#D78A8A\"><strong>[OFF]</strong></font>\n");
				return;
			}
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(47, 1, stringBuilder);
			handler.AppendLiteral("<font color=\"#F2F4F8\"><strong>");
			handler.AppendFormatted(value3);
			handler.AppendLiteral("</strong></font>\n");
			stringBuilder3.Append(ref handler);
		}
	}

	private static string FormatDuration(TimeSpan t)
	{
		string result;
		if (t.TotalDays >= 1.0)
		{
			result = $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
		}
		else
		{
			result = ((t.TotalHours >= 1.0) ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(0, t.Minutes)}m");
		}
		return result;
	}

	private static string GetStaticVersion()
	{
		try
		{
			return typeof(ICoreServerAPI).Assembly.GetName().Version?.ToString() ?? "";
		}
		catch
		{
			return "";
		}
	}

	private static void AddReflectedProperty(StringBuilder sb, string label, object? obj, string property)
	{
		if (obj == null)
		{
			return;
		}
		try
		{
			object obj2 = obj.GetType().GetProperty(property)?.GetValue(obj);
			if (obj2 != null)
			{
				if (obj2 is bool flag)
				{
					AddInfo(sb, label, flag ? "Enabled" : "Disabled");
				}
				else
				{
					AddInfo(sb, label, Convert.ToString(obj2, CultureInfo.InvariantCulture));
				}
			}
		}
		catch
		{
		}
	}

	private void AddMapSize(StringBuilder sb)
	{
		try
		{
			object blockAccessor = sapi.World.BlockAccessor;
			Type type = blockAccessor.GetType();
			object obj = type.GetProperty("MapSizeX")?.GetValue(blockAccessor);
			object obj2 = type.GetProperty("MapSizeZ")?.GetValue(blockAccessor);
			if (obj != null && obj2 != null)
			{
				AddInfo(sb, "World size", $"{obj} × {obj2} blocks");
			}
		}
		catch
		{
		}
	}

	private object? GetWorldConfigObject()
	{
		try
		{
			if (sapi?.World?.Config != null)
			{
				return sapi.World.Config;
			}
			return sapi?.WorldManager?.SaveGame?.WorldConfiguration;
		}
		catch
		{
			return null;
		}
	}

	private void AddWorldSetting(StringBuilder sb, string label, params string[] keys)
	{
		object worldConfigObject = GetWorldConfigObject();
		if (worldConfigObject == null)
		{
			return;
		}
		foreach (string key in keys)
		{
			string value = TryWorldConfigValue(worldConfigObject, key);
			if (!string.IsNullOrWhiteSpace(value))
			{
				AddInfo(sb, label, value);
				break;
			}
		}
	}

	private static string? TryWorldConfigValue(object wc, string key)
	{
		try
		{
			Type type = wc.GetType();
			(string, Type, object)[] array = new (string, Type, object)[4]
			{
				("GetString", typeof(string), ""),
				("GetInt", typeof(int), int.MinValue),
				("GetFloat", typeof(float), float.NaN),
				("GetDouble", typeof(double), double.NaN)
			};
			for (int i = 0; i < array.Length; i++)
			{
				(string, Type, object) tuple = array[i];
				MethodInfo method = type.GetMethod(tuple.Item1, new Type[2]
				{
					typeof(string),
					tuple.Item2
				});
				if (method == null)
				{
					continue;
				}
				object obj = method.Invoke(wc, new object[2] { key, tuple.Item3 });
				if (obj != null && (!(obj is int num) || num != int.MinValue) && (!(obj is float f) || !float.IsNaN(f)) && (!(obj is double d) || !double.IsNaN(d)))
				{
					string text = Convert.ToString(obj, CultureInfo.InvariantCulture) ?? "";
					if (bool.TryParse(text, out var result))
					{
						return result ? "Enabled" : "Disabled";
					}
					if (!string.IsNullOrWhiteSpace(text))
					{
						return text;
					}
				}
			}
			MethodInfo method2 = type.GetMethod("GetBool", new Type[2]
			{
				typeof(string),
				typeof(bool)
			});
			if (method2 != null)
			{
				MethodInfo method3 = type.GetMethod("HasAttribute", new Type[1] { typeof(string) });
				bool flag = default;
				int num2;
				if (method3 != null)
				{
					object obj2 = method3.Invoke(wc, new object[1] { key });
					if (obj2 is bool)
					{
						flag = (bool)obj2;
						num2 = 1;
					}
					else
					{
						num2 = 0;
					}
				}
				else
				{
					num2 = 0;
				}
				if (((uint)num2 & (flag ? 1u : 0u)) != 0)
				{
					return ((bool)method2.Invoke(wc, new object[2] { key, false })) ? "Enabled" : "Disabled";
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static string CompactModName(string value, int maxChars)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "Unknown";
		}
		string text = value.Trim();
		if (maxChars < 4 || text.Length <= maxChars)
		{
			return text;
		}
		return text.Substring(0, maxChars - 1).TrimEnd() + "…";
	}

	private static string WebEncode(string? value)
	{
		string text = WebUtility.HtmlDecode(value ?? "");
		return text.Replace("<", "＜").Replace(">", "＞");
	}

	private List<MotdPlayerEntry> BuildPlayerList()
	{
		if (sapi == null)
		{
			return new List<MotdPlayerEntry>();
		}
		return (from p in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
			where p != null && !string.IsNullOrWhiteSpace(p.PlayerName)
			select new MotdPlayerEntry
			{
				Name = p.PlayerName,
				Role = (p.Role?.Name ?? p.Role?.Code ?? "Player"),
				Ping = ((!float.IsNaN(p.Ping)) ? ((int)Math.Round(p.Ping * 1000f)) : 0),
				IsStaff = p.HasPrivilege(Privilege.controlserver)
			} into p
			orderby p.IsStaff descending
			select p).ThenBy((MotdPlayerEntry p) => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private (string title, string vtml) BuildVtml(MotdConfig config, IServerPlayer? player = null)
	{
		string text = (string.IsNullOrWhiteSpace(config.Title) ? "Message of the Day" : config.Title.Trim());
		List<string> lines = GetLines(config);
		string text2 = string.Join("\n", lines);
		string text3 = ((config.Format == MotdFormat.Markdown) ? MarkdownToVtml(text2) : text2) ?? "";
		text3 = LegacyMarkupToVtml(text3);
		text3 = text3.Replace("{playerName}", player?.PlayerName ?? "Player");
		string value = sapi?.Server?.Config?.ServerName ?? text;
		int valueOrDefault = (sapi?.World?.AllOnlinePlayers?.Count((IPlayer p) => p != null)).GetValueOrDefault();
		int valueOrDefault2 = (sapi?.Server?.Config?.MaxClients).GetValueOrDefault();
		text3 = text3.Replace("{{servername}}", WebEncode(value), StringComparison.OrdinalIgnoreCase).Replace("{{players}}", $"{valueOrDefault} / {valueOrDefault2}", StringComparison.OrdinalIgnoreCase).Replace("{{ingamedate}}", WebEncode(BuildInGameDate()), StringComparison.OrdinalIgnoreCase)
			.Replace("{{ingametime}}", WebEncode(BuildInGameTime()), StringComparison.OrdinalIgnoreCase)
			.Replace("{{season}}", WebEncode(BuildSeason()), StringComparison.OrdinalIgnoreCase)
			.Replace("{{serverversion}}", WebEncode(GetStaticVersion()), StringComparison.OrdinalIgnoreCase);
		text3 = text3.Replace("\r\n", "\n").Replace("\r", "\n");
		text3 = Regex.Replace(text3, "\\n{3,}", "\n\n");
		return (title: text, vtml: text3);
	}

	private static List<string> GetLines(MotdConfig c)
	{
		if (c.ContentLines != null && c.ContentLines.Count > 0)
		{
			return new List<string>(c.ContentLines);
		}
		if (!string.IsNullOrWhiteSpace(c.Content))
		{
			return c.Content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
				.ToList();
		}
		return new List<string>();
	}

	private void LoadConfig()
	{
		try
		{
			cfg = sapi.LoadModConfig<MotdConfig>("newfiescommunitymotd.json");
			if (cfg == null)
			{
				cfg = CreateFreshDefaults();
				sapi.StoreModConfig(cfg, "newfiescommunitymotd.json");
				sapi.Logger.Notification("[MOTD] Created new ModConfig/{0}", "newfiescommunitymotd.json");
			}
			else
			{
				sapi.Logger.Notification("[MOTD] Loaded ModConfig/{0}", "newfiescommunitymotd.json");
			}
			NormalizeConfig(cfg);
		}
		catch (Exception ex)
		{
			sapi.Logger.Error("[MOTD] LoadConfig error: {0}", ex);
			cfg = CreateFreshDefaults();
		}
	}

	private static MotdConfig CreateFreshDefaults()
	{
		return new MotdConfig
		{
			Title = "Newfies Community",
			Format = MotdFormat.VTML,
			ContentLines = new List<string>
			{
				"{{serverlogo}}", "<font size=\"22\" color=\"#FFC857\" align=\"center\"><strong>Welcome to Newfies Community, {playerName}!</strong></font>", "<font size=\"12\" color=\"#7FA7C0\" align=\"center\"><i>Example MOTD config showing different VTML formatting, including clickable links.</i></font>", "", "<font size=\"16\" color=\"#88CCFF\"><strong>Section Heading</strong></font>", "<font color=\"#D7DEE7\">This line is normal text with a <strong>bold section</strong>, an <i>italic note</i>, and a <font color=\"#9BE564\">green highlight</font>.</font>", "", "<font size=\"15\" color=\"#FFB347\"><strong>Rules Example</strong></font>", "<font color=\"#A7B3C2\"> -  Be respectful to other players.</font>", "<font color=\"#A7B3C2\"> -  No griefing, stealing, or cheating.</font>",
				"<font color=\"#FFC857\"> -  Ask staff if you are not sure about something.</font>", "", "<font size=\"15\" color=\"#F38BA8\"><strong>Announcement Example</strong></font>", "<font color=\"#F5C2E7\">Events every Saturday at 7 PM server time.</font>", "", "<font size=\"15\" color=\"#A6E3A1\"><strong>Hyperlink Examples</strong></font>", "<font color=\"#CDD6F4\">Visit the <a href=\"https://mods.vintagestory.at/show/mod/30458\">Newfies Community MOTD mod page</a>.</font>", "<font color=\"#CDD6F4\">Join our <a href=\"https://discord.gg/yourinvite\">Discord server</a>.</font>", "<font color=\"#7FA7C0\"><i>VTML link format example:</i> &lt;a href=\"https://example.com\"&gt;Link text&lt;/a&gt;</font>", "",
				"<font size=\"13\" color=\"#94E2D5\"><strong>Tips</strong></font>", "<font color=\"#CDD6F4\">Use <strong>/motd</strong> to reopen this window.</font>", "<font color=\"#CDD6F4\">Use <strong>/motdreload</strong> after editing the config.</font>", "", "<font size=\"11\" color=\"#666666\" align=\"center\">Edit this file at ModConfig/newfiescommunitymotd.json</font>"
			},
			Width = 980,
			Height = 720,
			OpenOnEveryJoin = false,
			OnlyOncePerSession = true,
			OnlyOncePerVersion = true,
			VersionLabel = DateTime.UtcNow.ToString("yyyy-MM-dd"),
			EnableDontShowAgain = true,
			ScrollBarWidth = 18,
			ScrollEstimateMultiplier = 1f,
			ScrollExtraPixels = 0,
			ScrollOverrideHeightPx = 0,
			EnableLinkButton = true,
			LinkButtonText = "Join our Discord",
			LinkUrl = "https://discord.gg/yourinvite",
			EnableWebMapButton = false,
			WebMapButtonText = "Web Map",
			WebMapUrl = "",
			Subtitle = "Server Information & Community Hub",
			AccentColor = "#FFC857",
			EnableLogo = false,
			LogoAsset = "newfiescommunitymotd:textures/gui/logo.png",
			LogoSource = "Asset",
			ServerLogoFile = "branding/motd-logo.png",
			LogoWidth = 180,
			LogoHeight = 0,
			LogoPreserveAspectRatio = true,
			EnablePlayersTab = false,
			ShowPlayerRoles = true,
			ShowPlayerPing = true,
			SeparateStaff = true,
			FooterText = "Use /motd any time to reopen this window.",
			EnableAnnouncementsTab = true,
			EnableUpdatesTab = true,
			EnableEventsTab = true,
			EnableRulesTab = true,
			EnableServerTab = true,
			EnableCommandsTab = true,
			EnableCommunityTab = false,
			ServerInfoVtml = "<font color=\"#CDD6F4\">Live server information is shown below. Add any server description, website, connection notes, or other details here.</font>",
			AnnouncementsVtml = "<font color=\"#CDD6F4\">Local announcements can appear here.</font>",
			UpdatesVtml = "",
			EventsVtml = "",
			RulesVtml = "<strong>Server Rules</strong>\nConfigure your rules in ModConfig/newfiescommunitymotd.json.",
			CommandsVtml = "<strong>/motd</strong> - Open this server hub.",
			CommunityVtml = "Configure your Discord and website links here.",
			UseDiscordAnnouncements = true,
			UseDiscordUpdates = true,
			UseDiscordEvents = true,
			MergeLocalAndDiscord = true,
			DiscordMaxItemsPerFeed = 10,
			UseSuiteStatus = true,
			ServerInfoShowServer = true,
			ServerInfoShowWorld = true,
			ServerInfoShowGameplay = true,
			ServerInfoShowProspecting = true,
			ServerInfoShowTemporal = true,
			ServerInfoShowMods = true
		};
	}

	private static void NormalizeConfig(MotdConfig c)
	{
		if (string.IsNullOrWhiteSpace(c.Title))
		{
			c.Title = "Message of the Day";
		}
		MotdConfig motdConfig = c;
		MotdFormat? format = motdConfig.Format;
		MotdFormat valueOrDefault = format.GetValueOrDefault();
		if (!format.HasValue)
		{
			valueOrDefault = MotdFormat.VTML;
			MotdConfig motdConfig2 = motdConfig;
			MotdFormat? format2 = valueOrDefault;
			motdConfig2.Format = format2;
		}
		motdConfig = c;
		int? width = motdConfig.Width;
		int valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 980;
			MotdConfig motdConfig3 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig3.Width = width2;
		}
		motdConfig = c;
		width = motdConfig.Height;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 720;
			MotdConfig motdConfig4 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig4.Height = width2;
		}
		motdConfig = c;
		bool? openOnEveryJoin = motdConfig.OpenOnEveryJoin;
		bool valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = false;
			MotdConfig motdConfig5 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig5.OpenOnEveryJoin = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.OnlyOncePerSession;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig6 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig6.OnlyOncePerSession = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.OnlyOncePerVersion;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig7 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig7.OnlyOncePerVersion = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableDontShowAgain;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig8 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig8.EnableDontShowAgain = openOnEveryJoin2;
		}
		motdConfig = c;
		width = motdConfig.ScrollBarWidth;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 18;
			MotdConfig motdConfig9 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig9.ScrollBarWidth = width2;
		}
		motdConfig = c;
		float? scrollEstimateMultiplier = motdConfig.ScrollEstimateMultiplier;
		float valueOrDefault4 = scrollEstimateMultiplier.GetValueOrDefault();
		if (!scrollEstimateMultiplier.HasValue)
		{
			valueOrDefault4 = 1f;
			MotdConfig motdConfig10 = motdConfig;
			float? scrollEstimateMultiplier2 = valueOrDefault4;
			motdConfig10.ScrollEstimateMultiplier = scrollEstimateMultiplier2;
		}
		motdConfig = c;
		width = motdConfig.ScrollExtraPixels;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 0;
			MotdConfig motdConfig11 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig11.ScrollExtraPixels = width2;
		}
		motdConfig = c;
		width = motdConfig.ScrollOverrideHeightPx;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 0;
			MotdConfig motdConfig12 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig12.ScrollOverrideHeightPx = width2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableLinkButton;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = false;
			MotdConfig motdConfig13 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig13.EnableLinkButton = openOnEveryJoin2;
		}
		motdConfig = c;
		if (motdConfig.LinkButtonText == null)
		{
			string text = (motdConfig.LinkButtonText = "Open Link");
		}
		motdConfig = c;
		if (motdConfig.LinkUrl == null)
		{
			string text = (motdConfig.LinkUrl = "");
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableWebMapButton;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = false;
			MotdConfig motdConfig14 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig14.EnableWebMapButton = openOnEveryJoin2;
		}
		motdConfig = c;
		if (motdConfig.WebMapButtonText == null)
		{
			string text = (motdConfig.WebMapButtonText = "Web Map");
		}
		motdConfig = c;
		if (motdConfig.WebMapUrl == null)
		{
			string text = (motdConfig.WebMapUrl = "");
		}
		motdConfig = c;
		if (motdConfig.Subtitle == null)
		{
			string text = (motdConfig.Subtitle = "");
		}
		motdConfig = c;
		if (motdConfig.AccentColor == null)
		{
			string text = (motdConfig.AccentColor = "#FFC857");
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableLogo;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = false;
			MotdConfig motdConfig15 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig15.EnableLogo = openOnEveryJoin2;
		}
		motdConfig = c;
		if (motdConfig.LogoAsset == null)
		{
			string text = (motdConfig.LogoAsset = "newfiescommunitymotd:textures/gui/logo.png");
		}
		motdConfig = c;
		if (motdConfig.LogoSource == null)
		{
			string text = (motdConfig.LogoSource = "Asset");
		}
		motdConfig = c;
		if (motdConfig.ServerLogoFile == null)
		{
			string text = (motdConfig.ServerLogoFile = "branding/motd-logo.png");
		}
		motdConfig = c;
		width = motdConfig.LogoWidth;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 180;
			MotdConfig motdConfig16 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig16.LogoWidth = width2;
		}
		motdConfig = c;
		width = motdConfig.LogoHeight;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 0;
			MotdConfig motdConfig17 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig17.LogoHeight = width2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.LogoPreserveAspectRatio;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig18 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig18.LogoPreserveAspectRatio = openOnEveryJoin2;
		}
		c.EnablePlayersTab = false;
		motdConfig = c;
		openOnEveryJoin = motdConfig.ShowPlayerRoles;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig19 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig19.ShowPlayerRoles = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ShowPlayerPing;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig20 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig20.ShowPlayerPing = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.SeparateStaff;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig21 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig21.SeparateStaff = openOnEveryJoin2;
		}
		motdConfig = c;
		if (motdConfig.FooterText == null)
		{
			string text = (motdConfig.FooterText = "");
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableAnnouncementsTab;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig22 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig22.EnableAnnouncementsTab = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableUpdatesTab;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig23 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig23.EnableUpdatesTab = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableEventsTab;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig24 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig24.EnableEventsTab = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableRulesTab;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig25 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig25.EnableRulesTab = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableServerTab;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig26 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig26.EnableServerTab = openOnEveryJoin2;
		}
		motdConfig = c;
		if (motdConfig.ServerInfoVtml == null)
		{
			string text = (motdConfig.ServerInfoVtml = "");
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.EnableCommandsTab;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig27 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig27.EnableCommandsTab = openOnEveryJoin2;
		}
		c.EnableCommunityTab = false;
		motdConfig = c;
		if (motdConfig.AnnouncementsVtml == null)
		{
			string text = (motdConfig.AnnouncementsVtml = "");
		}
		motdConfig = c;
		if (motdConfig.UpdatesVtml == null)
		{
			string text = (motdConfig.UpdatesVtml = "");
		}
		motdConfig = c;
		if (motdConfig.EventsVtml == null)
		{
			string text = (motdConfig.EventsVtml = "");
		}
		motdConfig = c;
		if (motdConfig.RulesVtml == null)
		{
			string text = (motdConfig.RulesVtml = "");
		}
		motdConfig = c;
		if (motdConfig.CommandsVtml == null)
		{
			string text = (motdConfig.CommandsVtml = "");
		}
		motdConfig = c;
		if (motdConfig.CommunityVtml == null)
		{
			string text = (motdConfig.CommunityVtml = "");
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.UseDiscordAnnouncements;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig28 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig28.UseDiscordAnnouncements = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.UseDiscordUpdates;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig29 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig29.UseDiscordUpdates = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.UseDiscordEvents;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig30 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig30.UseDiscordEvents = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.MergeLocalAndDiscord;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig31 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig31.MergeLocalAndDiscord = openOnEveryJoin2;
		}
		motdConfig = c;
		width = motdConfig.DiscordMaxItemsPerFeed;
		valueOrDefault2 = width.GetValueOrDefault();
		if (!width.HasValue)
		{
			valueOrDefault2 = 10;
			MotdConfig motdConfig32 = motdConfig;
			int? width2 = valueOrDefault2;
			motdConfig32.DiscordMaxItemsPerFeed = width2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.UseSuiteStatus;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig33 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig33.UseSuiteStatus = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ServerInfoShowServer;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig34 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig34.ServerInfoShowServer = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ServerInfoShowWorld;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig35 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig35.ServerInfoShowWorld = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ServerInfoShowGameplay;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig36 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig36.ServerInfoShowGameplay = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ServerInfoShowProspecting;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig37 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig37.ServerInfoShowProspecting = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ServerInfoShowTemporal;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig38 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig38.ServerInfoShowTemporal = openOnEveryJoin2;
		}
		motdConfig = c;
		openOnEveryJoin = motdConfig.ServerInfoShowMods;
		valueOrDefault3 = openOnEveryJoin == true;
		if (!openOnEveryJoin.HasValue)
		{
			valueOrDefault3 = true;
			MotdConfig motdConfig39 = motdConfig;
			bool? openOnEveryJoin2 = valueOrDefault3;
			motdConfig39.ServerInfoShowMods = openOnEveryJoin2;
		}
		if (string.IsNullOrEmpty(c.VersionLabel))
		{
			c.VersionLabel = DateTime.UtcNow.ToString("yyyy-MM-dd");
		}
	}

	private static string Sha256Hex(string s)
	{
		using SHA256 sHA = SHA256.Create();
		byte[] array = sHA.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
		StringBuilder stringBuilder = new StringBuilder(array.Length * 2);
		byte[] array2 = array;
		foreach (byte b in array2)
		{
			stringBuilder.Append(b.ToString("x2"));
		}
		return stringBuilder.ToString();
	}

	private static string MarkdownToVtml(string md)
	{
		if (string.IsNullOrEmpty(md))
		{
			return "";
		}
		string input = md;
		input = Regex.Replace(input, "^\\s*######\\s+(.*)$", "<font size=\"12\"><strong>$1</strong></font>", RegexOptions.Multiline);
		input = Regex.Replace(input, "^\\s*#####\\s+(.*)$", "<font size=\"14\"><strong>$1</strong></font>", RegexOptions.Multiline);
		input = Regex.Replace(input, "^\\s*####\\s+(.*)$", "<font size=\"16\"><strong>$1</strong></font>", RegexOptions.Multiline);
		input = Regex.Replace(input, "^\\s*###\\s+(.*)$", "<font size=\"18\"><strong>$1</strong></font>", RegexOptions.Multiline);
		input = Regex.Replace(input, "^\\s*##\\s+(.*)$", "<font size=\"20\"><strong>$1</strong></font>", RegexOptions.Multiline);
		input = Regex.Replace(input, "^\\s*#\\s+(.*)$", "<font size=\"22\"><strong>$1</strong></font>", RegexOptions.Multiline);
		input = Regex.Replace(input, "\\*\\*(.+?)\\*\\*", "<strong>$1</strong>");
		input = Regex.Replace(input, "\\*(.+?)\\*", "<i>$1</i>");
		input = Regex.Replace(input, "`(.+?)`", "<code>$1</code>");
		return Regex.Replace(input, "\\[(.+?)\\]\\((.+?)\\)", "$1");
	}

	private static string LegacyMarkupToVtml(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder();
		Stack<string> stack = new Stack<string>();
		int num = 0;
		while (num < text.Length)
		{
			if (text[num] == '[')
			{
				int num2 = text.IndexOf(']', num);
				if (num2 > num)
				{
					string text2 = text.Substring(num + 1, num2 - num - 1).Trim();
					if (text2.Equals("font-bold", StringComparison.OrdinalIgnoreCase) || text2.Equals("b", StringComparison.OrdinalIgnoreCase) || text2.Equals("strong", StringComparison.OrdinalIgnoreCase))
					{
						stringBuilder.Append("<strong>");
						stack.Push("</strong>");
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("/font", StringComparison.OrdinalIgnoreCase) || text2.Equals("/b", StringComparison.OrdinalIgnoreCase) || text2.Equals("/strong", StringComparison.OrdinalIgnoreCase))
					{
						if (stack.Count > 0)
						{
							stringBuilder.Append(stack.Pop());
						}
						num = num2 + 1;
						continue;
					}
					if (text2.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
					{
						string value = text2.Substring(5).Trim().Trim('"');
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder3 = stringBuilder2;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
						handler.AppendLiteral("<font size=\"");
						handler.AppendFormatted(value);
						handler.AppendLiteral("\">");
						stringBuilder3.Append(ref handler);
						stack.Push("</font>");
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("/size", StringComparison.OrdinalIgnoreCase))
					{
						if (stack.Count > 0)
						{
							stringBuilder.Append(stack.Pop());
						}
						num = num2 + 1;
						continue;
					}
					if (text2.StartsWith("color=", StringComparison.OrdinalIgnoreCase))
					{
						string value2 = text2.Substring(6).Trim().Trim('"');
						StringBuilder stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder4 = stringBuilder2;
						StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
						handler.AppendLiteral("<font color=\"");
						handler.AppendFormatted(value2);
						handler.AppendLiteral("\">");
						stringBuilder4.Append(ref handler);
						stack.Push("</font>");
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("/color", StringComparison.OrdinalIgnoreCase))
					{
						if (stack.Count > 0)
						{
							stringBuilder.Append(stack.Pop());
						}
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("small", StringComparison.OrdinalIgnoreCase))
					{
						stringBuilder.Append("<font size=\"11\">");
						stack.Push("</font>");
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("/small", StringComparison.OrdinalIgnoreCase))
					{
						if (stack.Count > 0)
						{
							stringBuilder.Append(stack.Pop());
						}
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("i", StringComparison.OrdinalIgnoreCase))
					{
						stringBuilder.Append("<i>");
						stack.Push("</i>");
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("/i", StringComparison.OrdinalIgnoreCase))
					{
						if (stack.Count > 0)
						{
							stringBuilder.Append(stack.Pop());
						}
						num = num2 + 1;
						continue;
					}
					if (text2.Equals("br", StringComparison.OrdinalIgnoreCase))
					{
						stringBuilder.Append("<br>");
						num = num2 + 1;
						continue;
					}
				}
			}
			stringBuilder.Append(text[num]);
			num++;
		}
		while (stack.Count > 0)
		{
			stringBuilder.Append(stack.Pop());
		}
		return stringBuilder.ToString();
	}
}
