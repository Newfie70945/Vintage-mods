using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace NewfiesPlayerList;

/// <summary>
/// Universal wrapper for the public Tab player list. The mod may be installed on a
/// server so Vintage Story distributes it to clients automatically, or installed by
/// an individual client on a server that does not use it. The server-side instance is
/// intentionally passive; all HUD behavior runs client-side.
/// </summary>
public sealed class NewfiesPlayerListModSystem : ModSystem
{
    private PublicPlayerListClient? client;
    private SupporterLampClient? supporterLamp;

    public override void Start(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Client) return;

        if (api.Network.GetChannel(PublicPlayerListClient.ChannelName) == null)
        {
            api.Network.RegisterChannel(PublicPlayerListClient.ChannelName)
                .RegisterMessageType<PublicPlayerListRequest>()
                .RegisterMessageType<PublicPlayerListClientHello>()
                .RegisterMessageType<PublicPlayerListResponse>();
        }

        if (api.Network.GetChannel(SupporterLampClient.ChannelName) == null)
        {
            api.Network.RegisterChannel(SupporterLampClient.ChannelName)
                .RegisterMessageType<SupporterLampHelloPacket>()
                .RegisterMessageType<SupporterLampStatePacket>();
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        client = new PublicPlayerListClient(api);
        client.Register();

        supporterLamp = new SupporterLampClient(api);
        supporterLamp.Register();
    }

    public override void Dispose()
    {
        supporterLamp?.Dispose();
        supporterLamp = null;

        client?.Dispose();
        client = null;
        base.Dispose();
    }
}
