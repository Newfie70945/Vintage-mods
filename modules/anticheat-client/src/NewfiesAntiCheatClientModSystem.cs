using Vintagestory.API.Client;
using Vintagestory.API.Common;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Packets;

namespace NewfiesAntiCheatClient;

public sealed class NewfiesAntiCheatClientModSystem : ModSystem
{
    private AntiCheatClientService? client;

    // Universal by design: server install is a required presence/version marker.
    // All scanning and client network behavior remains gated to the client side below.
    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void Start(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Client) return;

        if (api.Network.GetChannel(AntiCheatProtocol.ChannelName) == null)
        {
            api.Network.RegisterChannel(AntiCheatProtocol.ChannelName)
                .RegisterMessageType<ClientHelloPacket>()
                .RegisterMessageType<ServerChallengePacket>()
                .RegisterMessageType<ClientScanReportPacket>()
                .RegisterMessageType<ServerAckPacket>()
                .RegisterMessageType<HeartbeatPacket>()
                .RegisterMessageType<ServerModEvidenceRequestPacket>()
                .RegisterMessageType<ClientModEvidenceConsentPacket>()
                .RegisterMessageType<ServerModEvidenceConsentAckPacket>()
                .RegisterMessageType<ClientModEvidenceStartPacket>()
                .RegisterMessageType<ClientModEvidenceChunkPacket>()
                .RegisterMessageType<ClientModEvidenceCompletePacket>();
        }


        if (api.Network.GetChannel(OreShieldSyncProtocol.ChannelName) == null)
        {
            api.Network.RegisterChannel(OreShieldSyncProtocol.ChannelName)
                .RegisterMessageType<OreShieldSyncBatchPacket>()
                .RegisterMessageType<OreShieldSyncAckPacket>()
                .RegisterMessageType<OreShieldChunkSyncRequestPacket>()
                .RegisterMessageType<OreShieldSyncHelloPacket>();
        }

        if (api.Network.GetChannel(GhostVisibilityProtocol.ChannelName) == null)
        {
            api.Network.RegisterChannel(GhostVisibilityProtocol.ChannelName)
                .RegisterMessageType<GhostVisibilityPacket>();
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        client = new AntiCheatClientService(api);
        client.Start();
    }

    public override void Dispose()
    {
        client?.Dispose();
        client = null;
        base.Dispose();
    }
}
