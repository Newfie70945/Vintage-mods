using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesCommunityMOTD
{
    public class MotdServerSystem : ModSystem
    {
        private ICoreServerAPI? sapi;
        private MotdConfig? cfg;
        private readonly CachedLogoFile logoFile = new();
        private string logoFileHash = "";
        private EventBusListenerDelegate? controlListener;
        private readonly DateTime serverStartedUtc = DateTime.UtcNow;

        public const string ControlEvent = "newfiescommunitymotd:control";
        public const string PublicVersion = "2.1.6";
        private const string ConfigFileName = "newfiescommunitymotd.json";
        private const string ChannelName    = "newfiescommunitymotd";
        private const int MIN_W = 700, MAX_W = 1600, MIN_H = 520, MAX_H = 1000;

        public override bool ShouldLoad(EnumAppSide forSide) =>
            forSide == EnumAppSide.Server || forSide == EnumAppSide.Universal;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            LoadConfig();

            // Register the optional Discord bridge locally. It discovers Discord at runtime,
            // so Discord and Community MOTD remain independently buildable.
            NewfiesMotdIntegration.RegisterDiscord(new DiscordRuntimeAdapter());

            sapi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType(typeof(MotdPacket))
                .RegisterMessageType(typeof(MotdResetPacket))
                .SetMessageHandler<MotdPacket>(OnClientRequest);

            sapi.Event.PlayerNowPlaying += PlayerNowPlaying;
            // Modern ChatCommands API — no deprecation warnings
            sapi.ChatCommands
                .Create("motd")
                .WithDescription("Open the Message of the Day.")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(args =>
                {
                    if (args.Caller?.Player is IServerPlayer player)
                    {
                        SendMotd(player, true);
                        return TextCommandResult.Success();
                    }

                    return TextCommandResult.Error("Player only.");
                });

            sapi.ChatCommands
                .Create("motdreload")
                .WithDescription("Reload MOTD config from disk.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args =>
                {
                    LoadConfig();
                    return TextCommandResult.Success("[MOTD] Config reloaded.");
                });

            sapi.ChatCommands
                .Create("motdresetall")
                .WithDescription("Clear all client seen-MOTD caches.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args =>
                {
                    BroadcastSeenReset();
                    return TextCommandResult.Success("[MOTD] Reset broadcast sent.");
                });

            controlListener = OnSuiteControl;
            sapi.Event.RegisterEventBusListener(controlListener, filterByEventName: ControlEvent);

            sapi.Logger.Notification("[MOTD] Server started. v{0}; Suite companion control available.", PublicVersion);
        }

        private void OnSuiteControl(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (sapi == null || data is not ITreeAttribute tree) return;
            tree.SetBool("available", true);
            tree.SetString("version", PublicVersion);

            string operation = (tree.GetString("operation", "get") ?? "get").Trim().ToLowerInvariant();
            try
            {
                switch (operation)
                {
                    case "get":
                    case "status":
                        tree.SetBool("success", true);
                        tree.SetString("message", "Community MOTD companion is connected.");
                        tree.SetString("configJson", JsonConvert.SerializeObject(cfg ?? CreateFreshDefaults(), Formatting.None));
                        break;

                    case "apply":
                    case "save":
                    {
                        string json = tree.GetString("configJson", "") ?? "";
                        var next = JsonConvert.DeserializeObject<MotdConfig>(json);
                        if (next == null) throw new InvalidOperationException("MOTD configuration payload was empty.");
                        NormalizeConfig(next);
                        cfg = next;
                        sapi.StoreModConfig(cfg, ConfigFileName);
                        tree.SetBool("success", true);
                        tree.SetString("message", "Community MOTD configuration saved. Players receive it on the next MOTD open.");
                        tree.SetString("configJson", JsonConvert.SerializeObject(cfg, Formatting.None));
                        break;
                    }

                    case "reload":
                        LoadConfig();
                        tree.SetBool("success", true);
                        tree.SetString("message", "Community MOTD configuration reloaded from disk. Players receive it on the next MOTD open.");
                        tree.SetString("configJson", JsonConvert.SerializeObject(cfg ?? CreateFreshDefaults(), Formatting.None));
                        break;

                    case "resetseen":
                    case "reset-seen":
                        BroadcastSeenReset();
                        tree.SetBool("success", true);
                        tree.SetString("message", "Community MOTD seen-state reset broadcast sent.");
                        tree.SetString("configJson", JsonConvert.SerializeObject(cfg ?? CreateFreshDefaults(), Formatting.None));
                        break;

                    default:
                        tree.SetBool("success", false);
                        tree.SetString("message", "Unknown Community MOTD control operation: " + operation);
                        break;
                }
            }
            catch (Exception ex)
            {
                tree.SetBool("success", false);
                tree.SetString("message", ex.Message);
                sapi.Logger.Warning("[MOTD] Suite companion control '{0}' failed: {1}: {2}", operation, ex.GetType().Name, ex.Message);
            }

            handling = EnumHandling.PreventSubsequent;
        }

        private byte[] GetServerLogoBytes()
        {
            logoFileHash = "";
            if (sapi == null || cfg == null || !(cfg.EnableLogo ?? false) || !string.Equals(cfg.LogoSource, "ServerFile", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<byte>();
            try
            {
                string baseDir = sapi.GetOrCreateDataPath("NewfiesCommunityMOTD");
                string brandingDir = Path.Combine(baseDir, "branding");
                Directory.CreateDirectory(brandingDir);

                string configured = string.IsNullOrWhiteSpace(cfg.ServerLogoFile)
                    ? "branding/motd-logo.png"
                    : cfg.ServerLogoFile.Trim().Replace('\\', '/');

                // Only allow a relative path below the MOTD data directory.
                while (configured.StartsWith("/", StringComparison.Ordinal)) configured = configured.Substring(1);
                string relative = configured.Replace('/', Path.DirectorySeparatorChar);
                string path = Path.GetFullPath(Path.Combine(baseDir, relative));
                string safeRoot = Path.GetFullPath(baseDir + Path.DirectorySeparatorChar);
                if (!path.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                    return Array.Empty<byte>();

                // Backward compatibility for older installs that used NewfiesCommunityMOTD/motd-logo.png.
                if (!File.Exists(path)
                    && string.Equals(configured, "branding/motd-logo.png", StringComparison.OrdinalIgnoreCase))
                {
                    string legacyPath = Path.Combine(baseDir, "motd-logo.png");
                    if (File.Exists(legacyPath)) path = legacyPath;
                }

                if (!File.Exists(path)) return Array.Empty<byte>();
                var logo = logoFile.Read(path);
                logoFileHash = logo.Hash;
                return logo.Bytes;
            }
            catch (Exception ex) { sapi.Logger.Warning("[MOTD] Could not load server logo: {0}", ex.Message); return Array.Empty<byte>(); }
        }

        private void BroadcastSeenReset()
        {
            sapi?.Network.GetChannel(ChannelName)?.BroadcastPacket(new MotdResetPacket { ResetAll = true });
        }

        private void TryUnregisterControlListener()
        {
            if (sapi == null || controlListener == null) return;
            try
            {
                var method = sapi.Event.GetType().GetMethod("UnregisterEventBusListener", new[] { typeof(EventBusListenerDelegate) });
                method?.Invoke(sapi.Event, new object[] { controlListener });
            }
            catch { }
            controlListener = null;
        }

        public override void Dispose()
        {
            if (sapi != null)
            {
                try { sapi.Event.PlayerNowPlaying -= PlayerNowPlaying; } catch { }
                TryUnregisterControlListener();
            }
            sapi = null;
            base.Dispose();
        }

        private void PlayerNowPlaying(IServerPlayer player) => SendMotd(player, false);

        private void OnClientRequest(IServerPlayer player, MotdPacket pkt)
        {
            // Client requesting a resend (e.g. /motd command)
            SendMotd(player, true);
        }

        private void SendMotd(IServerPlayer player, bool forceShow)
        {
            if (cfg == null || sapi == null) return;
            var (title, vtml) = BuildVtml(cfg, player);
            if (string.IsNullOrWhiteSpace(vtml))
                vtml = "<strong>MOTD</strong>\n(Empty — edit ModConfig/newfiescommunitymotd.json)";

            int w = GameMath.Clamp(cfg.Width!.Value, MIN_W, MAX_W);
            int h = GameMath.Clamp(cfg.Height!.Value, MIN_H, MAX_H);

            var discordAnnouncements = NewfiesMotdIntegration.Discord?.GetAnnouncements();
            var discordUpdates = NewfiesMotdIntegration.Discord?.GetUpdates();

            var serverLogo = GetServerLogoBytes();
            var pkt = new MotdPacket
            {
                Title                    = string.IsNullOrWhiteSpace(title) ? "Message of the Day" : title,
                ContentVtml              = vtml,
                Width                    = w,
                Height                   = h,
                ScrollBarWidth           = GameMath.Clamp(cfg.ScrollBarWidth!.Value, 10, 30),
                ScrollEstimateMultiplier = Math.Max(0.5f, cfg.ScrollEstimateMultiplier!.Value),
                ScrollExtraPixels        = GameMath.Clamp(cfg.ScrollExtraPixels!.Value, -200, 200),
                ScrollOverrideHeightPx   = Math.Max(0, cfg.ScrollOverrideHeightPx!.Value),
                OpenOnEveryJoin          = cfg.OpenOnEveryJoin!.Value,
                OnlyOncePerSession       = cfg.OnlyOncePerSession!.Value,
                OnlyOncePerVersion       = cfg.OnlyOncePerVersion!.Value,
                VersionLabel             = cfg.VersionLabel ?? "",
                ContentHash              = Sha256Hex(vtml),
                ForceShow                = forceShow,
                IsRefresh                = false,
                EnableDontShowAgain      = cfg.EnableDontShowAgain ?? true,
                EnableLinkButton          = cfg.EnableLinkButton ?? false,
                LinkButtonText            = cfg.LinkButtonText ?? "Open Link",
                LinkUrl                   = cfg.LinkUrl ?? "",
                EnableWebMapButton         = cfg.EnableWebMapButton ?? false,
                WebMapButtonText           = cfg.WebMapButtonText ?? "Web Map",
                WebMapUrl                  = cfg.WebMapUrl ?? "",
                Subtitle                  = cfg.Subtitle ?? "",
                AccentColor               = cfg.AccentColor ?? "#FFC857",
                EnableLogo                = cfg.EnableLogo ?? false,
                LogoAsset                 = cfg.LogoAsset ?? "",
                LogoSource                = cfg.LogoSource ?? "Asset",
                ServerLogoHash            = logoFileHash,
                ServerLogoPng             = serverLogo,
                LogoWidth                 = GameMath.Clamp(cfg.LogoWidth ?? 180, 0, 400),
                LogoHeight                = GameMath.Clamp(cfg.LogoHeight ?? 0, 0, 400),
                LogoPreserveAspectRatio   = cfg.LogoPreserveAspectRatio ?? true,
                EnablePlayersTab          = cfg.EnablePlayersTab ?? true,
                ShowPlayerRoles           = cfg.ShowPlayerRoles ?? true,
                ShowPlayerPing            = cfg.ShowPlayerPing ?? true,
                SeparateStaff             = cfg.SeparateStaff ?? true,
                OnlineCount               = sapi.World.AllOnlinePlayers.Count(p => p != null),
                MaxPlayers                = sapi.Server.Config.MaxClients,
                InGameDate               = BuildInGameDate(),
                InGameTime               = BuildInGameTime(),
                Season                   = BuildSeason(),
                Players                   = BuildPlayerList(),
                FooterText                = cfg.FooterText ?? "",
                EnableAnnouncementsTab    = cfg.EnableAnnouncementsTab ?? true,
                EnableUpdatesTab          = cfg.EnableUpdatesTab ?? true,
                EnableEventsTab           = cfg.EnableEventsTab ?? true,
                EnableRulesTab            = cfg.EnableRulesTab ?? true,
                EnableServerTab           = cfg.EnableServerTab ?? true,
                EnableCommandsTab         = cfg.EnableCommandsTab ?? true,
                EnableCommunityTab        = false,
                AnnouncementsVtml         = BuildFeed("Announcements", cfg.AnnouncementsVtml, cfg.UseDiscordAnnouncements ?? true, discordAnnouncements),
                UpdatesVtml               = BuildFeed("Updates", cfg.UpdatesVtml, cfg.UseDiscordUpdates ?? true, discordUpdates),
                AnnouncementIds           = FeedIds(discordAnnouncements, cfg.UseDiscordAnnouncements ?? true, cfg.AnnouncementsVtml),
                UpdateIds                 = FeedIds(discordUpdates, cfg.UseDiscordUpdates ?? true, cfg.UpdatesVtml),
                EventsVtml                = BuildFeed("Events", cfg.EventsVtml, cfg.UseDiscordEvents ?? true, NewfiesMotdIntegration.Discord?.GetEvents()),
                RulesVtml                 = cfg.RulesVtml ?? "",
                ServerVtml                = BuildServerVtml(cfg.ServerInfoVtml),
                ServerInfoLeftVtml        = BuildServerInfoLeftVtml(cfg.ServerInfoVtml),
                ServerInfoRightVtml       = BuildServerInfoRightVtml(),
                CommandsVtml              = cfg.CommandsVtml ?? "",
                CommunityVtml             = cfg.CommunityVtml ?? "",
                DiscordConnected          = NewfiesMotdIntegration.Discord?.IsAvailable == true,
                SuiteConnected            = NewfiesMotdIntegration.Suite?.IsAvailable == true,
                IntegrationProtocol       = NewfiesMotdIntegration.ProtocolVersion
            };

            sapi.Logger.Debug("[MOTD] Sending on open to {0}: len={1}, force={2}", player.PlayerName, vtml.Length, forceShow);
            sapi.Network.GetChannel(ChannelName).SendPacket(pkt, player);
        }


        private string BuildInGameDate()
        {
            var calendar = sapi!.World.Calendar;
            if (calendar == null) return "Unavailable";

            // Use Vintage Story's own calendar formatter so month/day/year follow the world's calendar settings.
            string pretty = calendar.PrettyDate();
            int comma = pretty.LastIndexOf(',');
            if (comma > 0)
            {
                string tail = pretty.Substring(comma + 1);
                if (tail.Contains(":")) pretty = pretty.Substring(0, comma);
            }
            return pretty.Trim();
        }

        private string BuildInGameTime()
        {
            var calendar = sapi!.World.Calendar;
            if (calendar == null) return "";
            try
            {
                double hour = calendar.FullHourOfDay;
                int h = ((int)Math.Floor(hour) % 24 + 24) % 24;
                int m = Math.Clamp((int)Math.Floor((hour - Math.Floor(hour)) * 60.0), 0, 59);
                return $"{h:00}:{m:00}";
            }
            catch { return ""; }
        }

        private string BuildSeason()
        {
            var calendar = sapi!.World.Calendar;
            if (calendar == null) return "";
            try
            {
                // Derive a display-only season from the world's calendar month. This avoids
                // exposing location/world identity while giving the status strip useful context.
                int month = calendar.Month;
                int monthsPerYear = 12;
                try
                {
                    var prop = calendar.GetType().GetProperty("MonthsPerYear");
                    if (prop?.GetValue(calendar) is int value && value >= 4) monthsPerYear = value;
                }
                catch { }
                int normalized = ((month % monthsPerYear) + monthsPerYear) % monthsPerYear;
                int quarter = Math.Min(3, (normalized * 4) / monthsPerYear);
                return quarter switch { 0 => "Spring", 1 => "Summer", 2 => "Autumn", _ => "Winter" };
            }
            catch { return ""; }
        }

        private static string FeedKey(MotdFeedItem item)
        {
            string key = string.IsNullOrWhiteSpace(item.Id) ? $"{item.UnixTime}:{item.Title}" : item.Id;
            if (!string.IsNullOrWhiteSpace(item.Id) && key.Length <= 160 && !key.Any(c => c is '[' or ']' || char.IsControl(c))) return key;
            using var sha = SHA256.Create();
            return "entry:" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)));
        }

        private List<MotdFeedItem> OrderedFeed(IReadOnlyList<MotdFeedItem>? remote, bool enabled)
        {
            if (!enabled || remote == null) return new();
            int max = GameMath.Clamp(cfg?.DiscordMaxItemsPerFeed ?? 10, 1, 50);
            return remote.Take(200).Where(x => x != null).GroupBy(FeedKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(x => x.UnixTime).First())
                .OrderByDescending(x => x.UnixTime).ThenBy(FeedKey, StringComparer.Ordinal).Take(max).ToList();
        }

        private bool ShowLocalFeed(string? local, int remoteCount) => !string.IsNullOrWhiteSpace(local) &&
            ((cfg?.MergeLocalAndDiscord ?? true) || remoteCount == 0);

        private static string LocalFeedId(string local)
        {
            using var sha = SHA256.Create();
            return "local:" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(local.Trim())));
        }

        private List<string> FeedIds(IReadOnlyList<MotdFeedItem>? remote, bool enabled, string? local = null)
        {
            var entries = OrderedFeed(remote, enabled);
            var ids = entries.Select(FeedKey).ToList();
            if (ShowLocalFeed(local, entries.Count)) ids.Insert(0, LocalFeedId(local!));
            return ids;
        }

        private string BuildFeed(string heading, string? localVtml, bool useDiscord, IReadOnlyList<MotdFeedItem>? remote)
        {
            return MotdFeedFormatter.Build(heading, localVtml, useDiscord, remote,
                cfg?.DiscordMaxItemsPerFeed ?? 10, cfg?.AccentColor, cfg?.MergeLocalAndDiscord ?? true);
        }

        private string BuildServerVtml(string? customVtml)
        {
            // Fallback/single-column representation for older clients. Current clients render the
            // two live columns from ServerInfoLeftVtml and ServerInfoRightVtml.
            return BuildServerInfoLeftVtml(customVtml) + "\n\n" + BuildServerInfoRightVtml();
        }

        private string BuildServerInfoLeftVtml(string? customVtml)
        {
            if (sapi == null) return "";
            var sb = new StringBuilder();
            string accent = cfg?.AccentColor ?? "#FFC857";
            Section(sb, "SERVER", accent);
            if (!string.IsNullOrWhiteSpace(customVtml)) sb.Append(customVtml).Append("\n\n");

            if (cfg?.ServerInfoShowServer != false)
            {
                AddInfo(sb, "Server name", sapi.Server.Config.ServerName);
                AddInfo(sb, "Players", $"{sapi.World.AllOnlinePlayers.Count(p => p != null)} / {sapi.Server.Config.MaxClients}");
                AddInfo(sb, "Uptime", FormatDuration(DateTime.UtcNow - serverStartedUtc));
                AddInfo(sb, "Game version", GetStaticVersion());
                AddInfo(sb, "Community MOTD", PublicVersion);
                AddReflectedProperty(sb, "Port", sapi.Server.Config, "Port");
                AddReflectedProperty(sb, "Connection timeout", sapi.Server.Config, "ConnectionTimeout");
                AddReflectedProperty(sb, "Public listing", sapi.Server.Config, "AdvertiseServer");
                AddReflectedProperty(sb, "Server language", sapi.Server.Config, "ServerLanguage");
                AddReflectedProperty(sb, "Max clients", sapi.Server.Config, "MaxClients");
            }

            if (cfg?.ServerInfoShowGameplay != false)
            {
                Section(sb, "GAMEPLAY", "#8FCF8A");
                AddWorldSetting(sb, "Game mode", "gameMode", "gamemode");
                AddWorldSetting(sb, "Player health", "playerHealthPoints", "playerHealth");
                AddWorldSetting(sb, "Hunger speed", "hungerSpeed", "hungerSpeedModifier");
                AddWorldSetting(sb, "Creature strength", "creatureStrength", "creatureStrengthModifier");
                AddWorldSetting(sb, "Creature hostility", "creatureHostility");
                AddWorldSetting(sb, "Death punishment", "deathPunishment");
                AddWorldSetting(sb, "PvP", "allowPvP", "pvp");
                AddWorldSetting(sb, "Entity spawning", "entitySpawning", "allowEntitySpawning");
                AddWorldSetting(sb, "Class selection", "classExclusiveRecipes", "allowClassSelection");
                AddWorldSetting(sb, "Land claiming", "allowLandClaiming", "landClaiming");
                AddWorldSetting(sb, "Chiseling", "allowChiseling", "microblockChiseling");
                AddWorldSetting(sb, "Fire spread", "allowFireSpread", "fireSpread");
                AddWorldSetting(sb, "Block gravity", "blockGravity", "allowBlockGravity");
            }

            if (cfg?.ServerInfoShowTemporal != false)
            {
                Section(sb, "TEMPORAL", "#B69BE8");
                AddWorldSetting(sb, "Temporal stability", "temporalStability", "temporalStabilityEnabled");
                AddWorldSetting(sb, "Temporal storms", "temporalStorms", "temporalStormsEnabled");
                AddWorldSetting(sb, "Storm frequency", "temporalStormFrequency");
                AddWorldSetting(sb, "Temporal gear", "temporalGearRespawnUses", "temporalGear");
            }

            if (cfg?.UseSuiteStatus != false)
            {
                string status = GetSuiteStatusVtml();
                if (!string.IsNullOrWhiteSpace(status)) { Section(sb, "SUITE", "#7FA7C0"); sb.Append(status); }
            }
            return sb.ToString();
        }

        private string BuildServerInfoRightVtml()
        {
            if (sapi == null) return "";
            var sb = new StringBuilder();
            string accent = cfg?.AccentColor ?? "#FFC857";
            Section(sb, "WORLD", "#E2C46E");

            if (cfg?.ServerInfoShowWorld != false)
            {
                AddInfo(sb, "In-game date", BuildInGameDate());
                AddInfo(sb, "In-game time", BuildInGameTime());
                AddInfo(sb, "Season", BuildSeason());

                try
                {
                    var save = sapi.WorldManager?.SaveGame;
                    if (save != null)
                    {
                        AddInfo(sb, "Play style", save.PlayStyle);
                        AddInfo(sb, "World type", save.WorldType);
                        AddInfo(sb, "Entity spawning", save.EntitySpawning ? "Enabled" : "Disabled");
                        AddInfo(sb, "Last saved game version", save.LastSavedGameVersion);
                    }
                    if (sapi.WorldManager != null)
                    {
                        AddInfo(sb, "Chunk size", $"{sapi.WorldManager.ChunkSize} blocks");
                    }
                }
                catch { }

                var cal = sapi.World.Calendar;
                if (cal != null)
                {
                    AddInfo(sb, "Days per month", cal.DaysPerMonth.ToString());
                    AddReflectedProperty(sb, "Hours per day", cal, "HoursPerDay");
                    AddReflectedProperty(sb, "Calendar speed", cal, "SpeedOfTime");
                }
                AddMapSize(sb);
                AddWorldSetting(sb, "World height", "worldHeight");
                AddWorldSetting(sb, "Sea level", "seaLevel");
                AddWorldSetting(sb, "Landcover", "landcover");
                AddWorldSetting(sb, "Upheaval", "upheavalCommonness", "upheaval");
                AddWorldSetting(sb, "Geologic activity", "geoActivity", "geologicActivity");
                AddWorldSetting(sb, "Polar-equator distance", "polarEquatorDistance");
                AddWorldSetting(sb, "Starting climate", "startingClimate", "startingClimateMode");
                AddWorldSetting(sb, "Starting temperature", "startingTemperature");
                AddWorldSetting(sb, "Starting rainfall", "startingRainfall");
            }

            if (cfg?.ServerInfoShowProspecting != false)
            {
                Section(sb, "PROSPECTING & RESOURCES", "#78C7C9");
                AddWorldSetting(sb, "Propick node radius", "propickNodeSearchRadius", "propickNodeSearchRange", "propickRange");
                AddWorldSetting(sb, "Propick node mode", "propickNodeSearch", "propickNodeSearchEnabled");
                AddWorldSetting(sb, "Global deposit rate", "globalDepositSpawnRate", "depositSpawnRate");
                AddWorldSetting(sb, "Surface copper", "surfaceCopperDeposits", "surfaceCopper");
                AddWorldSetting(sb, "Surface tin", "surfaceTinDeposits", "surfaceTin");
                AddWorldSetting(sb, "Ore deposits", "oreDepositRate", "oreDeposits");
                AddWorldSetting(sb, "Tree growth speed", "treeGrowthSpeed", "saplingGrowthRate");
                AddWorldSetting(sb, "Food spoilage speed", "foodSpoilageSpeed", "foodSpoilageRate");
                AddWorldSetting(sb, "Tool durability", "toolDurability", "toolDurabilityModifier");
            }

            if (cfg?.ServerInfoShowMods != false)
            {
                var mods = sapi.ModLoader.Mods
                    .Where(m => m?.Info != null && !m.Info.CoreMod && !string.Equals(m.Info.ModID, "game", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(m => m.Info.ModID ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(m => string.IsNullOrWhiteSpace(m.Info.Name) ? m.Info.ModID : m.Info.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Keep the count in the section heading so the list starts immediately with useful content.
                Section(sb, $"INSTALLED MODS - {mods.Count}", "#9FB4C8");

                int index = 0;
                foreach (var mod in mods)
                {
                    index++;
                    string fullName = string.IsNullOrWhiteSpace(mod.Info.Name) ? (mod.Info.ModID ?? "Unknown") : mod.Info.Name;
                    string name = CompactModName(fullName, 38);
                    string version = string.IsNullOrWhiteSpace(mod.Info.Version) ? "" : mod.Info.Version;
                    string rowColor = (index % 2 == 0) ? "#D9E2EE" : "#F2F4F8";

                    sb.Append($"<font color=\"#6F8195\">{index:00}</font>  ");
                    sb.Append($"<font color=\"{rowColor}\"><strong>{WebEncode(name)}</strong></font>");
                    if (!string.IsNullOrWhiteSpace(version))
                        sb.Append($"  <font color=\"#7FA7C0\">v{WebEncode(version)}</font>");
                    sb.Append("\n");
                }

                if (mods.Count == 0)
                    sb.Append("<font color=\"#7FA7C0\"><i>No additional mods are loaded.</i></font>\n");
            }
            return sb.ToString();
        }

        private string GetSuiteStatusVtml()
        {
            if (sapi == null) return "";
            string status = "";
            try
            {
                var tree = new TreeAttribute();
                tree.SetBool("available", false); tree.SetInt("protocol", 1); tree.SetString("vtml", "");
                sapi.Event.PushEvent("newfiesserveressentials:motd-status", tree);
                if (tree.GetBool("available", false) && tree.GetInt("protocol", 0) == 1) status = tree.GetString("vtml", "") ?? "";
            }
            catch { }
            if (string.IsNullOrWhiteSpace(status) && NewfiesMotdIntegration.Suite?.IsAvailable == true) status = NewfiesMotdIntegration.Suite.GetPlayerFacingStatusVtml();
            return status;
        }

        private static void Section(StringBuilder sb, string title, string accent)
        {
            sb.Append($"\n<font size=\"14\" color=\"{accent}\"><strong>[ {WebEncode(title)} ]</strong></font>\n");
        }
        private static void AddInfo(StringBuilder sb, string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            string clean = value.Trim();
            string safeLabel = WebEncode(label);
            string safeValue = WebEncode(clean);

            sb.Append($"<font color=\"#9EADBD\">{safeLabel}</font>  ");

            if (string.Equals(clean, "Enabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(clean, "On", StringComparison.OrdinalIgnoreCase)
                || string.Equals(clean, "True", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<font color=\"#83C98C\"><strong>[ON]</strong></font>\n");
                return;
            }

            if (string.Equals(clean, "Disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(clean, "Off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(clean, "False", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("<font color=\"#D78A8A\"><strong>[OFF]</strong></font>\n");
                return;
            }

            sb.Append($"<font color=\"#F2F4F8\"><strong>{safeValue}</strong></font>\n");
        }
        private static string FormatDuration(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(0, t.Minutes)}m";
        private static string GetStaticVersion()
        {
            try { return typeof(ICoreServerAPI).Assembly.GetName().Version?.ToString() ?? ""; } catch { return ""; }
        }
        private static void AddReflectedProperty(StringBuilder sb, string label, object? obj, string property)
        {
            if (obj == null) return;
            try
            {
                var p = obj.GetType().GetProperty(property);
                var v = p?.GetValue(obj);
                if (v == null) return;
                if (v is bool b) AddInfo(sb, label, b ? "Enabled" : "Disabled");
                else AddInfo(sb, label, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }
        private void AddMapSize(StringBuilder sb)
        {
            try
            {
                object ba = sapi!.World.BlockAccessor;
                var t = ba.GetType();
                var x = t.GetProperty("MapSizeX")?.GetValue(ba); var z = t.GetProperty("MapSizeZ")?.GetValue(ba);
                if (x != null && z != null) AddInfo(sb, "World size", $"{x} × {z} blocks");
            }
            catch { }
        }
        private object? GetWorldConfigObject()
        {
            try
            {
                if (sapi?.World?.Config != null) return sapi.World.Config;
                return sapi?.WorldManager?.SaveGame?.WorldConfiguration;
            }
            catch { return null; }
        }
        private void AddWorldSetting(StringBuilder sb, string label, params string[] keys)
        {
            var wc = GetWorldConfigObject(); if (wc == null) return;
            foreach (string key in keys)
            {
                string? value = TryWorldConfigValue(wc, key);
                if (!string.IsNullOrWhiteSpace(value)) { AddInfo(sb, label, value); return; }
            }
        }
        private static string? TryWorldConfigValue(object wc, string key)
        {
            try
            {
                var t = wc.GetType();
                foreach (var spec in new[] { ("GetString", typeof(string), (object)""), ("GetInt", typeof(int), (object)int.MinValue), ("GetFloat", typeof(float), (object)float.NaN), ("GetDouble", typeof(double), (object)double.NaN) })
                {
                    var m = t.GetMethod(spec.Item1, new[] { typeof(string), spec.Item2 });
                    if (m == null) continue;
                    var v = m.Invoke(wc, new[] { (object)key, spec.Item3 });
                    if (v == null) continue;
                    if (v is int i && i == int.MinValue) continue;
                    if (v is float f && float.IsNaN(f)) continue;
                    if (v is double d && double.IsNaN(d)) continue;
                    string text = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (bool.TryParse(text, out bool parsedBool)) return parsedBool ? "Enabled" : "Disabled";
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                var bm = t.GetMethod("GetBool", new[] { typeof(string), typeof(bool) });
                if (bm != null)
                {
                    // Bool defaults cannot distinguish missing keys, so only use it if the attribute exists.
                    var hm = t.GetMethod("HasAttribute", new[] { typeof(string) });
                    if (hm != null && hm.Invoke(wc, new object[] { key }) is bool has && has)
                        return ((bool)bm.Invoke(wc, new object[] { key, false })!) ? "Enabled" : "Disabled";
                }
            }
            catch { }
            return null;
        }


        private static string CompactModName(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            string clean = value.Trim();
            if (maxChars < 4 || clean.Length <= maxChars) return clean;
            return clean.Substring(0, maxChars - 1).TrimEnd() + "…";
        }

        private static string WebEncode(string? value)
        {
            // Vintage Story rich text does not reliably decode HTML entities in visible text,
            // so entity-encoding '&' produced literal strings such as "&amp;" in Server Info.
            // Decode any pre-existing entities first, preserve normal punctuation/ampersands,
            // and neutralize only VTML tag delimiters with safe full-width characters.
            string text = System.Net.WebUtility.HtmlDecode(value ?? "");
            return text.Replace("<", "＜").Replace(">", "＞");
        }

        private List<MotdPlayerEntry> BuildPlayerList()
        {
            if (sapi == null) return new List<MotdPlayerEntry>();

            return sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.PlayerName))
                .Select(p => new MotdPlayerEntry
                {
                    Name = p.PlayerName,
                    Role = p.Role?.Name ?? p.Role?.Code ?? "Player",
                    Ping = float.IsNaN(p.Ping) ? 0 : (int)Math.Round(p.Ping * 1000f),
                    IsStaff = p.HasPrivilege(Privilege.controlserver)
                })
                .OrderByDescending(p => p.IsStaff)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private (string title, string vtml) BuildVtml(MotdConfig config, IServerPlayer? player = null)
        {
            string title = string.IsNullOrWhiteSpace(config.Title) ? "Message of the Day" : config.Title.Trim();
            var lines    = GetLines(config);
            string raw   = string.Join("\n", lines);
            string text  = (config.Format == MotdFormat.Markdown ? MarkdownToVtml(raw) : raw) ?? "";
            text = LegacyMarkupToVtml(text);
            text = text.Replace("{playerName}", player?.PlayerName ?? "Player");
            // Optional live Home VTML placeholders. These remain simple text substitutions so
            // Home stays a fully free-form VTML document controlled by the server owner.
            string serverName = sapi?.Server?.Config?.ServerName ?? title;
            int online = sapi?.World?.AllOnlinePlayers?.Count(p => p != null) ?? 0;
            int maxPlayers = sapi?.Server?.Config?.MaxClients ?? 0;
            text = text.Replace("{{servername}}", WebEncode(serverName), StringComparison.OrdinalIgnoreCase)
                       .Replace("{{players}}", $"{online} / {maxPlayers}", StringComparison.OrdinalIgnoreCase)
                       .Replace("{{ingamedate}}", WebEncode(BuildInGameDate()), StringComparison.OrdinalIgnoreCase)
                       .Replace("{{ingametime}}", WebEncode(BuildInGameTime()), StringComparison.OrdinalIgnoreCase)
                       .Replace("{{season}}", WebEncode(BuildSeason()), StringComparison.OrdinalIgnoreCase)
                       .Replace("{{serverversion}}", WebEncode(GetStaticVersion()), StringComparison.OrdinalIgnoreCase);
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return (title, text);
        }

        private static List<string> GetLines(MotdConfig c)
        {
            if (c.ContentLines != null && c.ContentLines.Count > 0)
                return new List<string>(c.ContentLines);
            if (!string.IsNullOrWhiteSpace(c.Content))
                return c.Content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            return new List<string>();
        }

        private void LoadConfig()
        {
            try
            {
                cfg = sapi!.LoadModConfig<MotdConfig>(ConfigFileName);
                if (cfg == null)
                {
                    cfg = CreateFreshDefaults();
                    sapi!.StoreModConfig(cfg, ConfigFileName);
                    sapi!.Logger.Notification("[MOTD] Created new ModConfig/{0}", ConfigFileName);
                }
                else
                {
                    sapi!.Logger.Notification("[MOTD] Loaded ModConfig/{0}", ConfigFileName);
                }
                NormalizeConfig(cfg);
            }
            catch (Exception ex)
            {
                sapi!.Logger.Error("[MOTD] LoadConfig error: {0}", ex);
                cfg = CreateFreshDefaults();
            }
        }

        private static MotdConfig CreateFreshDefaults() => new MotdConfig
        {
            Title  = "Newfies Community",
            Format = MotdFormat.VTML,
            ContentLines = new List<string>
            {
                @"{{serverlogo}}",
                @"<font size=""22"" color=""#FFC857"" align=""center""><strong>Welcome to Newfies Community, {playerName}!</strong></font>",
                @"<font size=""12"" color=""#7FA7C0"" align=""center""><i>Example MOTD config showing different VTML formatting, including clickable links.</i></font>",
                "",
                @"<font size=""16"" color=""#88CCFF""><strong>Section Heading</strong></font>",
                @"<font color=""#D7DEE7"">This line is normal text with a <strong>bold section</strong>, an <i>italic note</i>, and a <font color=""#9BE564"">green highlight</font>.</font>",
                "",
                @"<font size=""15"" color=""#FFB347""><strong>Rules Example</strong></font>",
                @"<font color=""#A7B3C2""> -  Be respectful to other players.</font>",
                @"<font color=""#A7B3C2""> -  No griefing, stealing, or cheating.</font>",
                @"<font color=""#FFC857""> -  Ask staff if you are not sure about something.</font>",
                "",
                @"<font size=""15"" color=""#F38BA8""><strong>Announcement Example</strong></font>",
                @"<font color=""#F5C2E7"">Events every Saturday at 7 PM server time.</font>",
                "",
                @"<font size=""15"" color=""#A6E3A1""><strong>Hyperlink Examples</strong></font>",
                @"<font color=""#CDD6F4"">Visit the <a href=""https://mods.vintagestory.at/show/mod/30458"">Newfies Community MOTD mod page</a>.</font>",
                @"<font color=""#CDD6F4"">Join our <a href=""https://discord.gg/yourinvite"">Discord server</a>.</font>",
                @"<font color=""#7FA7C0""><i>VTML link format example:</i> &lt;a href=""https://example.com""&gt;Link text&lt;/a&gt;</font>",
                "",
                @"<font size=""13"" color=""#94E2D5""><strong>Tips</strong></font>",
                @"<font color=""#CDD6F4"">Use <strong>/motd</strong> to reopen this window.</font>",
                @"<font color=""#CDD6F4"">Use <strong>/motdreload</strong> after editing the config.</font>",
                "",
                @"<font size=""11"" color=""#666666"" align=""center"">Edit this file at ModConfig/newfiescommunitymotd.json</font>"
            },
            Width                    = 980,
            Height                   = 720,
            OpenOnEveryJoin          = false,
            OnlyOncePerSession       = true,
            OnlyOncePerVersion       = true,
            VersionLabel             = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            EnableDontShowAgain      = true,
            ScrollBarWidth           = 18,
            ScrollEstimateMultiplier = 1.0f,
            ScrollExtraPixels        = 0,
            ScrollOverrideHeightPx   = 0,
            EnableLinkButton         = true,
            LinkButtonText           = "Join our Discord",
            LinkUrl                  = "https://discord.gg/yourinvite",
            EnableWebMapButton        = false,
            WebMapButtonText          = "Web Map",
            WebMapUrl                 = "",
            Subtitle                 = "Server Information & Community Hub",
            AccentColor              = "#FFC857",
            EnableLogo               = false,
            LogoAsset                = "newfiescommunitymotd:textures/gui/logo.png",
            LogoSource               = "Asset",
            ServerLogoFile           = "branding/motd-logo.png",
            LogoWidth                = 180,
            LogoHeight               = 0,
            LogoPreserveAspectRatio  = true,
            EnablePlayersTab         = false,
            ShowPlayerRoles          = true,
            ShowPlayerPing           = true,
            SeparateStaff            = true,
            FooterText               = "Use /motd any time to reopen this window.",
            EnableAnnouncementsTab = true, EnableUpdatesTab = true, EnableEventsTab = true, EnableRulesTab = true,
            EnableServerTab = true, EnableCommandsTab = true, EnableCommunityTab = false,
            ServerInfoVtml = "<font color=\"#CDD6F4\">Live server information is shown below. Add any server description, website, connection notes, or other details here.</font>",
            AnnouncementsVtml = "<font color=\"#CDD6F4\">Local announcements can appear here.</font>",
            UpdatesVtml = "", EventsVtml = "",
            RulesVtml = "<strong>Server Rules</strong>\nConfigure your rules in ModConfig/newfiescommunitymotd.json.",
            CommandsVtml = "<strong>/motd</strong> - Open this server hub.",
            CommunityVtml = "Configure your Discord and website links here.",
            UseDiscordAnnouncements = true, UseDiscordUpdates = true, UseDiscordEvents = true, MergeLocalAndDiscord = true,
            DiscordMaxItemsPerFeed = 10, UseSuiteStatus = true,
            ServerInfoShowServer = true, ServerInfoShowWorld = true, ServerInfoShowGameplay = true, ServerInfoShowProspecting = true, ServerInfoShowTemporal = true, ServerInfoShowMods = true
        };
        private static void NormalizeConfig(MotdConfig c)
        {
            if (string.IsNullOrWhiteSpace(c.Title)) c.Title = "Message of the Day";
            c.Format                   ??= MotdFormat.VTML;
            c.Width                    ??= 980;
            c.Height                   ??= 720;
            c.OpenOnEveryJoin          ??= false;
            c.OnlyOncePerSession       ??= true;
            c.OnlyOncePerVersion       ??= true;
            c.EnableDontShowAgain      ??= true;
            c.ScrollBarWidth           ??= 18;
            c.ScrollEstimateMultiplier ??= 1.0f;
            c.ScrollExtraPixels        ??= 0;
            c.ScrollOverrideHeightPx   ??= 0;
            c.EnableLinkButton         ??= false;
            c.LinkButtonText           ??= "Open Link";
            c.LinkUrl                  ??= "";
            c.EnableWebMapButton       ??= false;
            c.WebMapButtonText         ??= "Web Map";
            c.WebMapUrl                ??= "";
            c.Subtitle                 ??= "";
            c.AccentColor              ??= "#FFC857";
            c.EnableLogo               ??= false;
            c.LogoAsset                ??= "newfiescommunitymotd:textures/gui/logo.png";
            c.LogoSource               ??= "Asset";
            c.ServerLogoFile           ??= "branding/motd-logo.png";
            c.LogoWidth                ??= 180;
            c.LogoHeight               ??= 0;
            c.LogoPreserveAspectRatio  ??= true;
            c.EnablePlayersTab         = false;
            c.ShowPlayerRoles          ??= true;
            c.ShowPlayerPing           ??= true;
            c.SeparateStaff            ??= true;
            c.FooterText               ??= "";
            c.EnableAnnouncementsTab ??= true; c.EnableUpdatesTab ??= true; c.EnableEventsTab ??= true; c.EnableRulesTab ??= true;
            c.EnableServerTab ??= true; c.ServerInfoVtml ??= ""; c.EnableCommandsTab ??= true; c.EnableCommunityTab = false;
            c.AnnouncementsVtml ??= ""; c.UpdatesVtml ??= ""; c.EventsVtml ??= ""; c.RulesVtml ??= ""; c.CommandsVtml ??= ""; c.CommunityVtml ??= "";
            c.UseDiscordAnnouncements ??= true; c.UseDiscordUpdates ??= true; c.UseDiscordEvents ??= true; c.MergeLocalAndDiscord ??= true;
            c.DiscordMaxItemsPerFeed ??= 10; c.UseSuiteStatus ??= true;
            c.ServerInfoShowServer ??= true; c.ServerInfoShowWorld ??= true; c.ServerInfoShowGameplay ??= true; c.ServerInfoShowProspecting ??= true; c.ServerInfoShowTemporal ??= true; c.ServerInfoShowMods ??= true;
            if (string.IsNullOrEmpty(c.VersionLabel))
                c.VersionLabel = DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        private static string Sha256Hex(string s)
        {
            using var sha   = SHA256.Create();
            var bytes       = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
            var sb          = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string MarkdownToVtml(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var t = md;
            t = Regex.Replace(t, @"^\s*######\s+(.*)$", "<font size=\"12\"><strong>$1</strong></font>", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*#####\s+(.*)$",  "<font size=\"14\"><strong>$1</strong></font>", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*####\s+(.*)$",   "<font size=\"16\"><strong>$1</strong></font>", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*###\s+(.*)$",    "<font size=\"18\"><strong>$1</strong></font>", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*##\s+(.*)$",     "<font size=\"20\"><strong>$1</strong></font>", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*#\s+(.*)$",      "<font size=\"22\"><strong>$1</strong></font>", RegexOptions.Multiline);
            t = Regex.Replace(t, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            t = Regex.Replace(t, @"\*(.+?)\*",     "<i>$1</i>");
            t = Regex.Replace(t, @"`(.+?)`",        "<code>$1</code>");
            t = Regex.Replace(t, @"\[(.+?)\]\((.+?)\)", "$1");
            return t;
        }



        private static string LegacyMarkupToVtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var output = new StringBuilder();
            var stack = new Stack<string>();

            for (int i = 0; i < text.Length;)
            {
                if (text[i] == '[')
                {
                    int end = text.IndexOf(']', i);
                    if (end > i)
                    {
                        string tag = text.Substring(i + 1, end - i - 1).Trim();

                        if (tag.Equals("font-bold", StringComparison.OrdinalIgnoreCase) ||
                            tag.Equals("b", StringComparison.OrdinalIgnoreCase) ||
                            tag.Equals("strong", StringComparison.OrdinalIgnoreCase))
                        {
                            output.Append("<strong>");
                            stack.Push("</strong>");
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("/font", StringComparison.OrdinalIgnoreCase) ||
                            tag.Equals("/b", StringComparison.OrdinalIgnoreCase) ||
                            tag.Equals("/strong", StringComparison.OrdinalIgnoreCase))
                        {
                            if (stack.Count > 0) output.Append(stack.Pop());
                            i = end + 1;
                            continue;
                        }

                        if (tag.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
                        {
                            string size = tag.Substring(5).Trim().Trim('"');
                            output.Append($"<font size=\"{size}\">");
                            stack.Push("</font>");
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("/size", StringComparison.OrdinalIgnoreCase))
                        {
                            if (stack.Count > 0) output.Append(stack.Pop());
                            i = end + 1;
                            continue;
                        }

                        if (tag.StartsWith("color=", StringComparison.OrdinalIgnoreCase))
                        {
                            string color = tag.Substring(6).Trim().Trim('"');
                            output.Append($"<font color=\"{color}\">");
                            stack.Push("</font>");
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("/color", StringComparison.OrdinalIgnoreCase))
                        {
                            if (stack.Count > 0) output.Append(stack.Pop());
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("small", StringComparison.OrdinalIgnoreCase))
                        {
                            output.Append("<font size=\"11\">");
                            stack.Push("</font>");
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("/small", StringComparison.OrdinalIgnoreCase))
                        {
                            if (stack.Count > 0) output.Append(stack.Pop());
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("i", StringComparison.OrdinalIgnoreCase))
                        {
                            output.Append("<i>");
                            stack.Push("</i>");
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("/i", StringComparison.OrdinalIgnoreCase))
                        {
                            if (stack.Count > 0) output.Append(stack.Pop());
                            i = end + 1;
                            continue;
                        }

                        if (tag.Equals("br", StringComparison.OrdinalIgnoreCase))
                        {
                            output.Append("<br>");
                            i = end + 1;
                            continue;
                        }
                    }
                }

                output.Append(text[i]);
                i++;
            }

            while (stack.Count > 0) output.Append(stack.Pop());
            return output.ToString();
        }

    }
}
