using System;
using System.Linq;
using System.Reflection;
using BlockLogger.Protocol;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace BlockLogger.Frontend;

public sealed class InvestigatorClient : IDisposable
{
	private const string HotkeyCode = "blocklogger-investigator";

	private readonly ICoreClientAPI api;

	private IClientNetworkChannel? channel;

	private InvestigatorDialog? dialog;

	private long waypointDialogWatchListenerId;

	private AdminWaypointTeleportDialog? waypointTeleportDialog;

	private GuiDialog? activeNativeWaypointDialog;

	private int pendingWaypointX;

	private int pendingWaypointZ;

	private long pendingWaypointCaptureMs;

	public InvestigatorClient(ICoreClientAPI api)
	{
		this.api = api;
	}

	public void Register()
	{
		channel = api.Network.RegisterChannel("blocklogger-investigator-v2").RegisterMessageType<InvestigatorQueryRequest>().RegisterMessageType<InvestigatorQueryResponse>()
			.RegisterMessageType<InvestigatorStatusRequest>()
			.RegisterMessageType<InvestigatorStatusResponse>()
			.RegisterMessageType<BlockLoggerSettingsRequest>()
			.RegisterMessageType<BlockLoggerSettingsResponse>()
			.RegisterMessageType<AnimalLimiterRequest>()
			.RegisterMessageType<AnimalLimiterResponse>()
			.RegisterMessageType<ProfilerRequest>()
			.RegisterMessageType<ProfilerResponse>()
			.RegisterMessageType<LoginProtectionRequest>()
			.RegisterMessageType<LoginProtectionResponse>()
			.RegisterMessageType<WebDashboardRequest>()
			.RegisterMessageType<WebDashboardResponse>()
			.RegisterMessageType<AntiCheatGuiRequest>()
			.RegisterMessageType<AntiCheatGuiResponse>()
			.RegisterMessageType<AdminCommandGuiRequest>()
			.RegisterMessageType<AdminCommandGuiResponse>()
			.RegisterMessageType<DiscordIntegrationGuiRequest>()
			.RegisterMessageType<DiscordIntegrationGuiResponse>()
			.SetMessageHandler<InvestigatorQueryResponse>(OnQueryResponse)
			.SetMessageHandler<InvestigatorStatusResponse>(OnStatusResponse)
			.SetMessageHandler<BlockLoggerSettingsResponse>(OnBlockLoggerSettingsResponse)
			.SetMessageHandler<AnimalLimiterResponse>(OnAnimalLimiterResponse)
			.SetMessageHandler<ProfilerResponse>(OnProfilerResponse)
			.SetMessageHandler<LoginProtectionResponse>(OnLoginProtectionResponse)
			.SetMessageHandler<WebDashboardResponse>(OnWebDashboardResponse)
			.SetMessageHandler<AntiCheatGuiResponse>(OnAntiCheatResponse)
			.SetMessageHandler<AdminCommandGuiResponse>(OnAdminCommandResponse)
			.SetMessageHandler<DiscordIntegrationGuiResponse>(OnDiscordIntegrationResponse);
		api.Input.RegisterHotKey("blocklogger-investigator", "Open Newfies Server Essentials Suite", GlKeys.L, HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: false, shiftPressed: true);
		api.Input.SetHotKeyHandler("blocklogger-investigator", (KeyCombination _) => Toggle());
		api.Event.MouseDown += OnLowLevelMouseDown;
		waypointDialogWatchListenerId = api.Event.RegisterGameTickListener(WatchWaypointDialogs, 75);
	}

	private void OnLowLevelMouseDown(MouseEvent args)
	{
		if (args.Button != EnumMouseButton.Right)
		{
			return;
		}
		try
		{
			GuiDialogWorldMap guiDialogWorldMap = api.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
			if (guiDialogWorldMap != null && guiDialogWorldMap.IsOpened() && guiDialogWorldMap.DialogType == EnumDialogType.Dialog && guiDialogWorldMap.SingleComposer?.GetElement("mapElem") is GuiElementMap guiElementMap && guiElementMap.Bounds.PointInside(args.X, args.Y))
			{
				Vec3d worldPos = new Vec3d();
				float x = (float)args.X - (float)guiElementMap.Bounds.absX;
				float y = (float)args.Y - (float)guiElementMap.Bounds.absY;
				guiElementMap.TranslateViewPosToWorldPos(new Vec2f(x, y), ref worldPos);
				pendingWaypointX = (int)Math.Floor(worldPos.X);
				pendingWaypointZ = (int)Math.Floor(worldPos.Z);
				pendingWaypointCaptureMs = api.ElapsedMilliseconds;
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/WaypointTeleport] Could not capture map position: {0}", ex.Message);
		}
	}

	private void WatchWaypointDialogs(float _)
	{
		try
		{
			GuiDialog guiDialog = api.Gui.OpenedGuis.LastOrDefault((GuiDialog gui) => IsNativeWaypointDialog(gui));
			if (guiDialog == null)
			{
				CloseWaypointTeleportCompanion();
				activeNativeWaypointDialog = null;
				return;
			}
			if (guiDialog == activeNativeWaypointDialog)
			{
				AdminWaypointTeleportDialog? adminWaypointTeleportDialog = waypointTeleportDialog;
				if (adminWaypointTeleportDialog != null && adminWaypointTeleportDialog.IsOpened())
				{
					return;
				}
			}
			int x;
			int z;
			if (pendingWaypointCaptureMs > 0 && api.ElapsedMilliseconds - pendingWaypointCaptureMs <= 8000)
			{
				x = pendingWaypointX;
				z = pendingWaypointZ;
			}
			else if (!TryResolveWaypointDialogCoordinates(guiDialog, out x, out z))
			{
				return;
			}
			CloseWaypointTeleportCompanion();
			activeNativeWaypointDialog = guiDialog;
			waypointTeleportDialog = new AdminWaypointTeleportDialog(api, x, z, () =>
			{
				SendMapTeleport(x, z);
			});
			waypointTeleportDialog.TryOpen();
			api.Gui.RequestFocus(guiDialog);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/WaypointTeleport] Could not attach waypoint teleport action: {0}", ex.Message);
		}
	}

	private static bool IsNativeWaypointDialog(GuiDialog gui)
	{
		string name = gui.GetType().Name;
		return string.Equals(name, "GuiDialogAddWayPoint", StringComparison.Ordinal) || string.Equals(name, "GuiDialogEditWayPoint", StringComparison.Ordinal);
	}

	private static bool TryResolveWaypointDialogCoordinates(GuiDialog dialog, out int x, out int z)
	{
		x = 0;
		z = 0;
		object value = TryGetMember(dialog, "WorldPos") ?? TryGetMember(dialog, "worldPos") ?? TryGetMember(dialog, "Position") ?? TryGetMember(dialog, "position");
		if (TryExtractXZ(value, out x, out z))
		{
			return true;
		}
		object obj = TryGetMember(dialog, "waypoint") ?? TryGetMember(dialog, "Waypoint");
		if (obj != null)
		{
			value = TryGetMember(obj, "Position") ?? TryGetMember(obj, "position");
			if (TryExtractXZ(value, out x, out z))
			{
				return true;
			}
		}
		return false;
	}

	private static object? TryGetMember(object instance, string name)
	{
		Type type = instance.GetType();
		try
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null && property.GetIndexParameters().Length == 0)
			{
				return property.GetValue(instance);
			}
			FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				return field.GetValue(instance);
			}
		}
		catch
		{
		}
		return null;
	}

	private static bool TryExtractXZ(object? value, out int x, out int z)
	{
		x = 0;
		z = 0;
		if (value is Vec3d vec3d)
		{
			x = (int)Math.Floor(vec3d.X);
			z = (int)Math.Floor(vec3d.Z);
			return true;
		}
		if (value is BlockPos blockPos)
		{
			x = blockPos.X;
			z = blockPos.Z;
			return true;
		}
		return false;
	}

	private void CloseWaypointTeleportCompanion()
	{
		if (waypointTeleportDialog != null)
		{
			waypointTeleportDialog.TryClose();
			waypointTeleportDialog.Dispose();
			waypointTeleportDialog = null;
		}
	}

	private bool Toggle()
	{
		if (dialog == null)
		{
			dialog = new InvestigatorDialog(api, SendQuery, SendStatus, SendBlockLoggerSettings, SendAnimalLimiter, SendProfiler, SendLoginProtection, SendWebDashboard, SendAntiCheat, SendAdminCommand, SendDiscordIntegration);
		}
		if (dialog.IsOpened())
		{
			dialog.TryClose();
		}
		else
		{
			dialog.TryOpen();
		}
		return true;
	}

	private void SendQuery(InvestigatorQueryRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendStatus(InvestigatorStatusRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendBlockLoggerSettings(BlockLoggerSettingsRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendAnimalLimiter(AnimalLimiterRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendProfiler(ProfilerRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendLoginProtection(LoginProtectionRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendWebDashboard(WebDashboardRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendAntiCheat(AntiCheatGuiRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendAdminCommand(AdminCommandGuiRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendDiscordIntegration(DiscordIntegrationGuiRequest request)
	{
		channel?.SendPacket(request);
	}

	private void SendMapTeleport(int x, int z)
	{
		if (channel == null)
		{
			api.ShowChatMessage("[Server Essentials] The admin network channel is not ready. Reconnect and try the map teleport again.");
			api.Logger.Warning("[ServerEssentials/AdminMapTeleport] Admin channel was null while sending map teleport to {0},{1}.", x, z);
		}
		else
		{
			channel.SendPacket(new AdminCommandGuiRequest
			{
				RequestId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Operation = "map-teleport",
				IntValue = x,
				IntValue2 = z
			});
		}
	}

	private void OnQueryResponse(InvestigatorQueryResponse response)
	{
		dialog?.Show(response);
	}

	private void OnStatusResponse(InvestigatorStatusResponse response)
	{
		dialog?.Show(response);
	}

	private void OnBlockLoggerSettingsResponse(BlockLoggerSettingsResponse response)
	{
		dialog?.Show(response);
	}

	private void OnAnimalLimiterResponse(AnimalLimiterResponse response)
	{
		dialog?.Show(response);
	}

	private void OnProfilerResponse(ProfilerResponse response)
	{
		dialog?.Show(response);
	}

	private void OnLoginProtectionResponse(LoginProtectionResponse response)
	{
		dialog?.Show(response);
	}

	private void OnWebDashboardResponse(WebDashboardResponse response)
	{
		dialog?.Show(response);
	}

	private void OnAntiCheatResponse(AntiCheatGuiResponse response)
	{
		dialog?.Show(response);
	}

	private void OnDiscordIntegrationResponse(DiscordIntegrationGuiResponse response)
	{
		dialog?.Show(response);
	}

	private void OnAdminCommandResponse(AdminCommandGuiResponse response)
	{
		if (string.Equals(response.Operation, "map-teleport", StringComparison.OrdinalIgnoreCase))
		{
			api.ShowChatMessage("[Server Essentials] " + response.Message);
		}
		else
		{
			dialog?.Show(response);
		}
	}

	public void Dispose()
	{
		api.Event.MouseDown -= OnLowLevelMouseDown;
		if (waypointDialogWatchListenerId != 0)
		{
			api.Event.UnregisterGameTickListener(waypointDialogWatchListenerId);
			waypointDialogWatchListenerId = 0L;
		}
		CloseWaypointTeleportCompanion();
		activeNativeWaypointDialog = null;
		dialog?.Dispose();
		dialog = null;
	}
}
