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

    // Waypoint teleport integration intentionally uses the low-level client mouse event plus
    // IGuiAPI.OpenedGuis instead of replacing the world-map right-click path. This keeps native
    // waypoint placement/editing intact and avoids version-dependent waypoint dialog members.
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
        channel = api.Network.RegisterChannel(InvestigatorServerBridge.ChannelName)
            .RegisterMessageType<InvestigatorQueryRequest>()
            .RegisterMessageType<InvestigatorQueryResponse>()
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

        api.Input.RegisterHotKey(
            HotkeyCode,
            "Open Newfies Server Essentials Suite",
            GlKeys.L,
            HotkeyType.GUIOrOtherControls,
            shiftPressed: true);

        api.Input.SetHotKeyHandler(HotkeyCode, _ => Toggle());

        // Keep Vintage Story's native right-click behavior untouched. We only remember the
        // clicked map position and, when the game opens its Add/Edit Waypoint dialog, attach a
        // small Teleport Here companion panel beside it. MouseDown is used so the coordinate is
        // captured before the vanilla dialog consumes the corresponding MouseUp.
        api.Event.MouseDown += OnLowLevelMouseDown;
        waypointDialogWatchListenerId = api.Event.RegisterGameTickListener(WatchWaypointDialogs, 75);
    }

    private void OnLowLevelMouseDown(MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Right) return;

        try
        {
            var worldMap = api.ModLoader.GetModSystem<WorldMapManager>();
            var worldMapDialog = worldMap?.worldMapDlg;
            if (worldMapDialog == null || !worldMapDialog.IsOpened() || worldMapDialog.DialogType != EnumDialogType.Dialog) return;

            var mapElement = worldMapDialog.SingleComposer?.GetElement("mapElem") as GuiElementMap;
            if (mapElement == null || !mapElement.Bounds.PointInside(args.X, args.Y)) return;

            Vec3d worldPosition = new();
            float clickX = args.X - (float)mapElement.Bounds.absX;
            float clickY = args.Y - (float)mapElement.Bounds.absY;
            mapElement.TranslateViewPosToWorldPos(new Vec2f(clickX, clickY), ref worldPosition);

            pendingWaypointX = (int)Math.Floor(worldPosition.X);
            pendingWaypointZ = (int)Math.Floor(worldPosition.Z);
            pendingWaypointCaptureMs = api.ElapsedMilliseconds;
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
            GuiDialog? nativeDialog = api.Gui.OpenedGuis
                .LastOrDefault(gui => IsNativeWaypointDialog(gui));

            if (nativeDialog == null)
            {
                CloseWaypointTeleportCompanion();
                activeNativeWaypointDialog = null;
                return;
            }

            if (ReferenceEquals(nativeDialog, activeNativeWaypointDialog) && waypointTeleportDialog?.IsOpened() == true) return;

            int x;
            int z;
            bool recentClick = pendingWaypointCaptureMs > 0 && api.ElapsedMilliseconds - pendingWaypointCaptureMs <= 8000;
            if (recentClick)
            {
                x = pendingWaypointX;
                z = pendingWaypointZ;
            }
            else if (!TryResolveWaypointDialogCoordinates(nativeDialog, out x, out z))
            {
                // No trustworthy coordinate means no teleport button. Never guess a destination.
                return;
            }

            CloseWaypointTeleportCompanion();
            activeNativeWaypointDialog = nativeDialog;
            waypointTeleportDialog = new AdminWaypointTeleportDialog(api, x, z, () => SendMapTeleport(x, z));
            waypointTeleportDialog.TryOpen();

            // Keep the vanilla editor as the keyboard-focused dialog so waypoint name typing and
            // all existing controls behave exactly as before. The companion remains mouse-clickable.
            api.Gui.RequestFocus(nativeDialog);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/WaypointTeleport] Could not attach waypoint teleport action: {0}", ex.Message);
        }
    }

    private static bool IsNativeWaypointDialog(GuiDialog gui)
    {
        string name = gui.GetType().Name;
        return string.Equals(name, "GuiDialogAddWayPoint", StringComparison.Ordinal)
            || string.Equals(name, "GuiDialogEditWayPoint", StringComparison.Ordinal);
    }

    private static bool TryResolveWaypointDialogCoordinates(GuiDialog dialog, out int x, out int z)
    {
        x = 0;
        z = 0;
        object? candidate = TryGetMember(dialog, "WorldPos")
            ?? TryGetMember(dialog, "worldPos")
            ?? TryGetMember(dialog, "Position")
            ?? TryGetMember(dialog, "position");

        if (TryExtractXZ(candidate, out x, out z)) return true;

        // Edit waypoint dialogs commonly retain a Waypoint object. Resolve its Position via
        // reflection so this still compiles against VSEssentials builds where the member layout
        // differs from the public source snapshot.
        object? waypoint = TryGetMember(dialog, "waypoint") ?? TryGetMember(dialog, "Waypoint");
        if (waypoint != null)
        {
            candidate = TryGetMember(waypoint, "Position") ?? TryGetMember(waypoint, "position");
            if (TryExtractXZ(candidate, out x, out z)) return true;
        }

        return false;
    }

    private static object? TryGetMember(object instance, string name)
    {
        Type type = instance.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(instance);

            FieldInfo? field = type.GetField(name, flags);
            if (field != null) return field.GetValue(instance);
        }
        catch
        {
            // Optional compatibility probe only. The recent map click remains the primary source.
        }
        return null;
    }

    private static bool TryExtractXZ(object? value, out int x, out int z)
    {
        x = 0;
        z = 0;
        if (value is Vec3d vec)
        {
            x = (int)Math.Floor(vec.X);
            z = (int)Math.Floor(vec.Z);
            return true;
        }
        if (value is BlockPos pos)
        {
            x = pos.X;
            z = pos.Z;
            return true;
        }
        return false;
    }

    private void CloseWaypointTeleportCompanion()
    {
        if (waypointTeleportDialog == null) return;
        waypointTeleportDialog.TryClose();
        waypointTeleportDialog.Dispose();
        waypointTeleportDialog = null;
    }

    private bool Toggle()
    {
        dialog ??= new InvestigatorDialog(api, SendQuery, SendStatus, SendBlockLoggerSettings, SendAnimalLimiter, SendProfiler, SendLoginProtection, SendWebDashboard, SendAntiCheat, SendAdminCommand, SendDiscordIntegration);
        if (dialog.IsOpened()) dialog.TryClose();
        else dialog.TryOpen();
        return true;
    }

    private void SendQuery(InvestigatorQueryRequest request) => channel?.SendPacket(request);
    private void SendStatus(InvestigatorStatusRequest request) => channel?.SendPacket(request);
    private void SendBlockLoggerSettings(BlockLoggerSettingsRequest request) => channel?.SendPacket(request);
    private void SendAnimalLimiter(AnimalLimiterRequest request) => channel?.SendPacket(request);
    private void SendProfiler(ProfilerRequest request) => channel?.SendPacket(request);
    private void SendLoginProtection(LoginProtectionRequest request) => channel?.SendPacket(request);
    private void SendWebDashboard(WebDashboardRequest request) => channel?.SendPacket(request);
    private void SendAntiCheat(AntiCheatGuiRequest request) => channel?.SendPacket(request);
    private void SendAdminCommand(AdminCommandGuiRequest request) => channel?.SendPacket(request);
    private void SendDiscordIntegration(DiscordIntegrationGuiRequest request) => channel?.SendPacket(request);

    private void SendMapTeleport(int x, int z)
    {
        if (channel == null)
        {
            api.ShowChatMessage("[Server Essentials] The admin network channel is not ready. Reconnect and try the map teleport again.");
            api.Logger.Warning("[ServerEssentials/AdminMapTeleport] Admin channel was null while sending map teleport to {0},{1}.", x, z);
            return;
        }

        channel.SendPacket(new AdminCommandGuiRequest
        {
            RequestId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Operation = "map-teleport",
            IntValue = x,
            IntValue2 = z
        });
    }
    private void OnQueryResponse(InvestigatorQueryResponse response) => dialog?.Show(response);
    private void OnStatusResponse(InvestigatorStatusResponse response) => dialog?.Show(response);
    private void OnBlockLoggerSettingsResponse(BlockLoggerSettingsResponse response) => dialog?.Show(response);
    private void OnAnimalLimiterResponse(AnimalLimiterResponse response) => dialog?.Show(response);
    private void OnProfilerResponse(ProfilerResponse response) => dialog?.Show(response);
    private void OnLoginProtectionResponse(LoginProtectionResponse response) => dialog?.Show(response);
    private void OnWebDashboardResponse(WebDashboardResponse response) => dialog?.Show(response);
    private void OnAntiCheatResponse(AntiCheatGuiResponse response) => dialog?.Show(response);
    private void OnDiscordIntegrationResponse(DiscordIntegrationGuiResponse response) => dialog?.Show(response);
    private void OnAdminCommandResponse(AdminCommandGuiResponse response)
    {
        if (string.Equals(response.Operation, "map-teleport", StringComparison.OrdinalIgnoreCase))
        {
            api.ShowChatMessage($"[Server Essentials] {response.Message}");
            return;
        }

        dialog?.Show(response);
    }

    public void Dispose()
    {
        api.Event.MouseDown -= OnLowLevelMouseDown;
        if (waypointDialogWatchListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(waypointDialogWatchListenerId);
            waypointDialogWatchListenerId = 0;
        }
        CloseWaypointTeleportCompanion();
        activeNativeWaypointDialog = null;
        dialog?.Dispose();
        dialog = null;
    }
}
