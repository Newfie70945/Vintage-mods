using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace NewfiesChatGuard;

public sealed class ChatGuardGuiClient : IDisposable
{
    public const string HotkeyCode = "newfies-chatguard-admin";

    private readonly ICoreClientAPI api;
    private IClientNetworkChannel? channel;
    private ChatGuardDialog? dialog;
    private long requestId;
    private long lastAppliedRequestId;
    private bool openRequested;
    private bool disposed;

    public ChatGuardGuiClient(ICoreClientAPI api) => this.api = api;

    public void Register()
    {
        channel = api.Network.RegisterChannel(ChatGuardGuiServer.ChannelName)
            .RegisterMessageType<ChatGuardGuiRequest>()
            .RegisterMessageType<ChatGuardGuiResponse>()
            .SetMessageHandler<ChatGuardGuiResponse>(OnResponse);

        if (api.Input.GetHotKeyByCode(HotkeyCode) == null)
        {
            api.Input.RegisterHotKey(
                HotkeyCode,
                "Open Newfie's ChatGuard admin GUI",
                GlKeys.G,
                HotkeyType.GUIOrOtherControls,
                shiftPressed: true);
        }
        api.Input.SetHotKeyHandler(HotkeyCode, _ => Toggle());
    }

    public bool Toggle()
    {
        if (disposed) return false;
        if (dialog?.IsOpened() == true)
        {
            dialog.TryClose();
            return true;
        }
        openRequested = true;
        return Send(new ChatGuardGuiRequest { Operation = "snapshot" });
    }

    public bool Refresh() => Send(new ChatGuardGuiRequest { Operation = "snapshot" });

    public bool Action(string action, string target, int minutes, bool permanent, string reason) => Send(new ChatGuardGuiRequest
    {
        Operation = "action",
        Action = action,
        Target = target,
        Minutes = minutes,
        Permanent = permanent,
        Reason = reason
    });

    public bool Test(string target, string message) => Send(new ChatGuardGuiRequest
    {
        Operation = "test",
        Target = target,
        TestMessage = message
    });

    public bool Save(string configJson) => Send(new ChatGuardGuiRequest
    {
        Operation = "save",
        ConfigJson = configJson
    });

    private bool Send(ChatGuardGuiRequest request)
    {
        if (disposed) return false;
        if (channel == null || SafeChannelState() != EnumChannelState.Connected)
        {
            openRequested = false;
            api.ShowChatMessage("[ChatGuard] Standalone admin GUI is unavailable. The server must run ChatGuard 2.1.1+ and your account needs staff access.");
            return false;
        }
        request.RequestId = ++requestId;
        try
        {
            channel.SendPacket(request);
            return true;
        }
        catch (Exception ex)
        {
            openRequested = false;
            api.Logger.Debug("[ChatGuard] Admin request could not be sent: {0}", ex.Message);
            api.ShowChatMessage("[ChatGuard] The server connection changed. Reopen the admin GUI after reconnecting.");
            return false;
        }
    }

    private EnumChannelState SafeChannelState()
    {
        try { return api.Network.GetChannelState(ChatGuardGuiServer.ChannelName); }
        catch { return EnumChannelState.NotFound; }
    }

    private void OnResponse(ChatGuardGuiResponse response)
    {
        if (disposed || response == null) return;
        // A server-requested open uses its own ID, whereas replies to client
        // operations use our counter. Never let a delayed older reply replace a
        // newer moderation result or a saved configuration.
        if (!response.OpenDialog)
        {
            if (response.RequestId < lastAppliedRequestId) return;
            lastAppliedRequestId = response.RequestId;
        }
        dialog ??= new ChatGuardDialog(api, this);
        dialog.Update(response);
        if (openRequested || response.OpenDialog)
        {
            openRequested = false;
            if (!dialog.IsOpened()) dialog.TryOpen();
        }
        if (!response.Success && !string.IsNullOrWhiteSpace(response.Message))
            api.ShowChatMessage("[ChatGuard] " + response.Message);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        openRequested = false;
        var hotkey = api.Input.GetHotKeyByCode(HotkeyCode);
        if (hotkey != null) hotkey.Handler = null!;
        try { dialog?.TryClose(); } catch { }
        if (dialog != null)
        {
            dialog.Dispose();
            api.Gui.LoadedGuis.Remove(dialog);
        }
        dialog = null;
        channel = null;
    }
}
