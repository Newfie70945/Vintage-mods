using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace NewfiesChatGuard;

public sealed class ChatGuardGuiServer : IDisposable
{
    public const string ChannelName = "newfies-chatguard-admin-v1";

    private readonly ICoreServerAPI api;
    private readonly ChatGuardService service;
    private IServerNetworkChannel? channel;

    public ChatGuardGuiServer(ICoreServerAPI api, ChatGuardService service)
    {
        this.api = api;
        this.service = service;
    }

    public void Register()
    {
        channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<ChatGuardGuiRequest>()
            .RegisterMessageType<ChatGuardGuiResponse>()
            .SetMessageHandler<ChatGuardGuiRequest>(OnRequest);
    }

    public void OpenFor(IServerPlayer player)
    {
        if (channel == null) return;
        if (!HasAdminAccess(player))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                "[ChatGuard] You need the controlserver privilege to open the admin GUI.",
                EnumChatType.CommandError);
            return;
        }

        try
        {
            var response = service.ProcessGuiRequest(player, new ChatGuardGuiRequest
            {
                RequestId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Operation = "snapshot"
            });
            response.OpenDialog = true;
            channel.SendPacket(response, player);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ChatGuard] Could not open standalone admin GUI for {0}: {1}", player.PlayerName, ex.Message);
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                "[ChatGuard] The standalone admin GUI could not be opened. Make sure the ChatGuard client component is installed.",
                EnumChatType.CommandError);
        }
    }

    private void OnRequest(IServerPlayer player, ChatGuardGuiRequest request)
    {
        if (channel == null) return;
        if (!HasAdminAccess(player))
        {
            channel.SendPacket(new ChatGuardGuiResponse
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "You need the controlserver privilege to use the ChatGuard admin GUI."
            }, player);
            return;
        }

        ChatGuardGuiResponse response;
        try
        {
            response = service.ProcessGuiRequest(player, request);
        }
        catch (Exception ex)
        {
            response = new ChatGuardGuiResponse
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "ChatGuard GUI request failed: " + ex.Message,
                SnapshotJson = service.BuildGuiSnapshotJson()
            };
        }
        channel.SendPacket(response, player);
    }

    private static bool HasAdminAccess(IServerPlayer player) =>
        player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root);

    public void Dispose()
    {
        channel = null;
    }
}
