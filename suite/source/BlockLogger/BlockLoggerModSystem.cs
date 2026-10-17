using System;
using BlockLogger.Commands;
using BlockLogger.Config;
using BlockLogger.Frontend;
using BlockLogger.PlayerList;
using BlockLogger.Services;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger;

public sealed class BlockLoggerModSystem : ModSystem
{
	private ICoreServerAPI? serverApi;

	private readonly BlockLoggerConfig config = new BlockLoggerConfig();

	private SuiteFeatureConfig features = new SuiteFeatureConfig();

	private BlockLoggerModule? module;

	private InvestigatorClient? investigatorClient;

	private EventBusListenerDelegate? motdStatusListener;

	private const string MotdStatusEvent = "newfiesserveressentials:motd-status";

	public override bool ShouldLoad(EnumAppSide side)
	{
		return true;
	}

	public override void Start(ICoreAPI api)
	{
		if (api.Network.GetChannel("newfies-public-playerlist-v1") == null)
		{
			api.Network.RegisterChannel("newfies-public-playerlist-v1").RegisterMessageType<PublicPlayerListRequest>().RegisterMessageType<PublicPlayerListClientHello>()
				.RegisterMessageType<PublicPlayerListResponse>();
		}
		if (api.Side == EnumAppSide.Server && api.Network.GetChannel("newfies-supporter-lamp-v1") == null)
		{
			api.Network.RegisterChannel("newfies-supporter-lamp-v1").RegisterMessageType<SupporterLampHelloPacket>().RegisterMessageType<SupporterLampStatePacket>();
		}
		if (api.Side == EnumAppSide.Server && api.Network.GetChannel("newfiesghostvisibility") == null)
		{
			api.Network.RegisterChannel("newfiesghostvisibility").RegisterMessageType<GhostVisibilityPacket>();
		}
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		serverApi = api;
		SuiteConfigStorage.InitializeAndRelocateLegacyFiles(api);
		LoadConfig();
		features = SuiteFeatureConfig.Load(api);
		SuiteFeatureConfig suiteFeatureConfig = features.Clone();
		new SuiteFeatureCommands(api, features, suiteFeatureConfig).Register();
		foreach (Mod mod in api.ModLoader.Mods)
		{
			if (mod?.Info == null || !string.Equals(mod.Info.ModID, "blocklogger", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			api.Logger.Error("[ServerEssentials] Legacy BlockLogger conflict detected ({0}). Remove the old blocklogger/NewfiesBlockloggingSuite ZIP before running Newfies Server Essentials Suite; both mods otherwise execute together and can duplicate logging, diagnostics, and Harmony integrations.", mod.FileName ?? "blocklogger");
			break;
		}
		foreach (Mod mod2 in api.ModLoader.Mods)
		{
			if (mod2?.Info == null || !string.Equals(mod2.Info.ModID, "newfiesanticheatserver", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			api.Logger.Error("[ServerEssentials] Standalone Newfie's Anti-Cheat Server conflict detected ({0}). Anti-cheat server networking is integrated into Server Essentials Suite as of 1.5.7. Remove the standalone newfiesanticheatserver ZIP to avoid duplicate ownership of the newfiesanticheat channel.", mod2.FileName ?? "newfiesanticheatserver");
			break;
		}
		module = new BlockLoggerModule(api, config, suiteFeatureConfig, features, ReloadConfig);
		module.Start();
		motdStatusListener = OnMotdStatusRequest;
		api.Event.RegisterEventBusListener(motdStatusListener, 0.5, "newfiesserveressentials:motd-status");
		if (suiteFeatureConfig.IngameAdminGui)
		{
			InvestigatorServerBridge investigatorServerBridge = new InvestigatorServerBridge(api, module);
			investigatorServerBridge.Register();
		}
		else
		{
			api.Logger.Notification("[ServerEssentials] In-game administration GUI disabled by {0}.", "NewfiesServerEssentialsFeatures.json");
		}
		api.Logger.Notification("[ServerEssentials] Feature gates active. Disabled={0}", string.IsNullOrWhiteSpace(suiteFeatureConfig.DisabledSummary()) ? "none" : suiteFeatureConfig.DisabledSummary());
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		investigatorClient = new InvestigatorClient(api);
		investigatorClient.Register();
	}

	public override void Dispose()
	{
		TryUnregisterMotdStatusListener();
		investigatorClient?.Dispose();
		investigatorClient = null;
		module?.Dispose();
		module = null;
		base.Dispose();
	}

	private void OnMotdStatusRequest(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (serverApi != null && data is ITreeAttribute treeAttribute)
		{
			treeAttribute.SetBool("available", value: true);
			treeAttribute.SetInt("protocol", 1);
			int num = serverApi.World.AllOnlinePlayers?.Length ?? 0;
			treeAttribute.SetString("vtml", $"<strong>Suite:</strong> <font color='#8FDA72'>Online</font> · {num} player{((num == 1) ? "" : "s")} connected");
			handling = EnumHandling.PreventSubsequent;
		}
	}

	private void TryUnregisterMotdStatusListener()
	{
		if (serverApi != null && motdStatusListener != null)
		{
			try
			{
				serverApi.Event.GetType().GetMethod("UnregisterEventBusListener", new Type[1] { typeof(EventBusListenerDelegate) })?.Invoke(serverApi.Event, new object[1] { motdStatusListener });
			}
			catch
			{
			}
			motdStatusListener = null;
		}
	}

	private void ReloadConfig()
	{
		LoadConfig();
		module?.ApplyRuntimeSettings();
		serverApi?.Logger.Notification("[BlockLogger] Configuration reloaded.");
	}

	private void LoadConfig()
	{
		if (serverApi == null)
		{
			return;
		}
		BlockLoggerConfig blockLoggerConfig = ModConfigRecovery.LoadOrNull<BlockLoggerConfig>(serverApi, "NewfiesBlocklogger.json", "BlockLogger");
		if (blockLoggerConfig == null)
		{
			blockLoggerConfig = ModConfigRecovery.LoadOrNull<BlockLoggerConfig>(serverApi, "blocklogger-v2.json", "BlockLogger");
			if (blockLoggerConfig != null)
			{
				serverApi.Logger.Notification("[BlockLogger] Migrating configuration from {0} to {1}.", "blocklogger-v2.json", "NewfiesBlocklogger.json");
			}
		}
		if (blockLoggerConfig == null)
		{
			blockLoggerConfig = new BlockLoggerConfig();
		}
		blockLoggerConfig.SchemaVersion = Math.Max(3, blockLoggerConfig.SchemaVersion);
		blockLoggerConfig.Clamp();
		config.ApplyFrom(blockLoggerConfig);
		SuiteConfigStorage.Store(serverApi, config, "NewfiesBlocklogger.json");
	}
}
