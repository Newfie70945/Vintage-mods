using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace NewfiesChatGuard;

public sealed class NewfiesChatGuardModSystem : ModSystem
{
    private ChatGuardService? service;
    private ChatGuardGuiServer? guiServer;
    private ChatGuardGuiClient? guiClient;

    public override void StartServerSide(ICoreServerAPI api)
    {
        service = new ChatGuardService(api);
        guiServer = new ChatGuardGuiServer(api, service);
        guiServer.Register();
        service.SetStandaloneGuiOpener(guiServer.OpenFor);
        service.Start();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        guiClient = new ChatGuardGuiClient(api);
        guiClient.Register();
    }

    public override void Dispose()
    {
        guiClient?.Dispose();
        guiClient = null;
        guiServer?.Dispose();
        guiServer = null;
        service?.Dispose();
        service = null;
        base.Dispose();
    }
}
