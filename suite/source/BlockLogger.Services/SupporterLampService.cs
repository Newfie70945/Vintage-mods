using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class SupporterLampService : IDisposable
{
	private sealed class SupporterLampStateModel
	{
		public List<string> EnabledPlayerUids { get; set; } = new List<string>();
	}

	public const string ChannelName = "newfies-supporter-lamp-v1";

	public const string PrivilegeCode = "supporterlamp";

	public const byte LightHue = 33;

	public const byte LightSaturation = 7;

	public const byte LightBrightness = 16;

	public const string WatchedEnabledKey = "nssSupporterLampEnabled";

	public const string WatchedHueKey = "nssSupporterLampHue";

	public const string WatchedSaturationKey = "nssSupporterLampSaturation";

	public const string WatchedBrightnessKey = "nssSupporterLampBrightness";

	private const string StateFile = "NewfiesSupporterLampState.json";

	private readonly ICoreServerAPI api;

	private readonly HashSet<string> enabledUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> readyClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private IServerNetworkChannel? channel;

	private bool started;

	public SupporterLampService(ICoreServerAPI api)
	{
		this.api = api;
	}

	public void Start()
	{
		if (!started)
		{
			started = true;
			LoadState();
			api.Permissions.RegisterPrivilege("supporterlamp", "Allows the player to toggle the supporter /lamp light perk.");
			channel = api.Network.GetChannel("newfies-supporter-lamp-v1") ?? api.Network.RegisterChannel("newfies-supporter-lamp-v1").RegisterMessageType<SupporterLampHelloPacket>().RegisterMessageType<SupporterLampStatePacket>();
			channel.SetMessageHandler<SupporterLampHelloPacket>(OnClientHello);
			if (api.ChatCommands.Get("lamp") == null)
			{
				api.ChatCommands.Create("lamp").WithDescription("Toggle your supporter lamp.").RequiresPlayer()
					.RequiresPrivilege("supporterlamp")
					.HandleWith(OnLampCommand);
			}
			else
			{
				api.Logger.Warning("[ServerEssentials/SupporterLamp] /lamp is already registered by another mod; supporter lamp command was not registered.");
			}
			api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
			api.Event.PlayerDisconnect += OnPlayerDisconnect;
			api.Logger.Notification("[ServerEssentials/SupporterLamp] Ready. Privilege={0}, light HSV={1}/{2}/{3}. Player state defaults OFF.", "supporterlamp", (byte)33, (byte)7, (byte)16);
		}
	}

	private TextCommandResult OnLampCommand(TextCommandCallingArgs args)
	{
		if (!(args.Caller.Player is IServerPlayer serverPlayer))
		{
			return TextCommandResult.Error("This command can only be used by a player.");
		}
		bool flag;
		lock (enabledUids)
		{
			if (enabledUids.Contains(serverPlayer.PlayerUID))
			{
				enabledUids.Remove(serverPlayer.PlayerUID);
				flag = false;
			}
			else
			{
				enabledUids.Add(serverPlayer.PlayerUID);
				flag = true;
			}
		}
		SaveState();
		PublishEntityState(serverPlayer, flag);
		BroadcastState(serverPlayer.PlayerUID, flag);
		bool flag2;
		lock (readyClients)
		{
			flag2 = readyClients.Contains(serverPlayer.PlayerUID);
		}
		if (flag && !flag2)
		{
			return TextCommandResult.Success("Supporter lamp enabled. Player List has not completed the optional lamp handshake yet; synchronized entity state will still be used when a compatible renderer is installed.");
		}
		return TextCommandResult.Success(flag ? "Supporter lamp enabled. Use /lamp again to turn it off." : "Supporter lamp disabled.");
	}

	private void OnClientHello(IServerPlayer player, SupporterLampHelloPacket packet)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		if (packet == null || packet.ProtocolVersion != 1)
		{
			api.Logger.Warning("[ServerEssentials/SupporterLamp] Ignoring incompatible lamp renderer hello from {0}; protocol={1}.", player.PlayerName, packet?.ProtocolVersion ?? (-1));
			return;
		}
		lock (readyClients)
		{
			readyClients.Add(player.PlayerUID);
		}
		api.Logger.Debug("[ServerEssentials/SupporterLamp] Lamp renderer ready for {0} ({1}).", player.PlayerName, player.PlayerUID);
		string[] array;
		lock (enabledUids)
		{
			array = enabledUids.ToArray();
		}
		string[] array2 = array;
		foreach (string playerUid in array2)
		{
			if (api.World.PlayerByUid(playerUid) is IServerPlayer { ConnectionState: EnumClientState.Connected } serverPlayer && serverPlayer.HasPrivilege("supporterlamp"))
			{
				SendState(player, playerUid, enabled: true);
			}
		}
	}

	private void OnPlayerNowPlaying(IServerPlayer player)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		bool flag;
		lock (enabledUids)
		{
			flag = enabledUids.Contains(player.PlayerUID);
		}
		if (flag && !player.HasPrivilege("supporterlamp"))
		{
			lock (enabledUids)
			{
				enabledUids.Remove(player.PlayerUID);
			}
			SaveState();
			flag = false;
		}
		PublishEntityState(player, flag);
		BroadcastState(player.PlayerUID, flag);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			lock (readyClients)
			{
				readyClients.Remove(player.PlayerUID);
			}
			PublishEntityState(player, enabled: false);
			BroadcastState(player.PlayerUID, enabled: false);
		}
	}

	private static void PublishEntityState(IServerPlayer player, bool enabled)
	{
		try
		{
			if (player?.Entity?.WatchedAttributes != null)
			{
				player.Entity.WatchedAttributes.SetBool("nssSupporterLampEnabled", enabled);
				player.Entity.WatchedAttributes.SetInt("nssSupporterLampHue", 33);
				player.Entity.WatchedAttributes.SetInt("nssSupporterLampSaturation", 7);
				player.Entity.WatchedAttributes.SetInt("nssSupporterLampBrightness", 16);
			}
		}
		catch
		{
		}
	}

	private void BroadcastState(string playerUid, bool enabled)
	{
		if (channel == null || string.IsNullOrWhiteSpace(playerUid))
		{
			return;
		}
		IServerPlayer[] array;
		lock (readyClients)
		{
			array = (from p in api.World.AllOnlinePlayers.OfType<IServerPlayer>()
				where p.ConnectionState == EnumClientState.Connected && readyClients.Contains(p.PlayerUID)
				select p).ToArray();
		}
		IServerPlayer[] array2 = array;
		foreach (IServerPlayer recipient in array2)
		{
			SendState(recipient, playerUid, enabled);
		}
	}

	private void SendState(IServerPlayer recipient, string playerUid, bool enabled)
	{
		try
		{
			channel?.SendPacket(new SupporterLampStatePacket
			{
				PlayerUid = playerUid,
				Enabled = enabled,
				Hue = 33,
				Saturation = 7,
				Brightness = 16
			}, recipient);
		}
		catch (Exception ex)
		{
			api.Logger.Debug("[ServerEssentials/SupporterLamp] State send to {0} skipped: {1}: {2}", recipient.PlayerName, ex.GetType().Name, ex.Message);
		}
	}

	private void LoadState()
	{
		try
		{
			SupporterLampStateModel supporterLampStateModel = SuiteConfigStorage.Load<SupporterLampStateModel>(api, "NewfiesSupporterLampState.json") ?? new SupporterLampStateModel();
			lock (enabledUids)
			{
				enabledUids.Clear();
				foreach (string item in supporterLampStateModel.EnabledPlayerUids ?? new List<string>())
				{
					if (!string.IsNullOrWhiteSpace(item))
					{
						enabledUids.Add(item.Trim());
					}
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/SupporterLamp] Could not load {0}; starting with all lamps off. {1}: {2}", "NewfiesSupporterLampState.json", ex.GetType().Name, ex.Message);
		}
	}

	private void SaveState()
	{
		try
		{
			string[] source;
			lock (enabledUids)
			{
				source = enabledUids.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToArray();
			}
			SuiteConfigStorage.Store(api, new SupporterLampStateModel
			{
				EnabledPlayerUids = source.ToList()
			}, "NewfiesSupporterLampState.json");
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/SupporterLamp] Could not save {0}: {1}: {2}", "NewfiesSupporterLampState.json", ex.GetType().Name, ex.Message);
		}
	}

	public void Dispose()
	{
		if (!started)
		{
			return;
		}
		started = false;
		try
		{
			api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
		}
		catch
		{
		}
		try
		{
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
		}
		catch
		{
		}
		lock (readyClients)
		{
			readyClients.Clear();
		}
	}
}
