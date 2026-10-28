using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BlockLogger.Services;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace BlockLogger.SeedPrivacy;

public sealed class SeedPrivacyConfig
{
    public bool Enabled { get; set; } = false;
    public int PublicSeed { get; set; } = 0;
}

// Restart-only, independent from anti-cheat live enable/disable. This never
// changes IWorldAccessor.Seed, SaveGame, native weather objects or region storage.
public sealed class SeedPrivacySystem : ModSystem
{
    private const string ConfigFile = "NewfiesSeedPrivacy.json";
    private const string HarmonyId = "newfies.serveressentials.seedprivacy";
    private static readonly System.Func<object, object> CopyObject = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<System.Func<object, object>>();
    private static SeedPrivacySystem? current;
    private ICoreServerAPI? api;
    private SeedPrivacyConfig config = new();
    private Harmony? harmony;
    private bool disposed, active;
    private int weatherChannel, weatherMessage;
    private long identifications, weatherPackets, regionPackets, failures, lastError;
    private string status = "Not started.";

    public override double ExecuteOrder() => 0.99; // Suite config relocation runs first.
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI serverApi)
    {
        api = serverApi;
        try
        {
            config = SuiteConfigStorage.Load<SeedPrivacyConfig>(api, ConfigFile) ?? new();
            SuiteConfigStorage.Store(api, config, ConfigFile);
        }
        catch (Exception e) { status = "Configuration unavailable: " + e.GetType().Name; api.Logger.Warning("[SeedPrivacy] {0}", status); return; }
        api.ChatCommands.Create("seedprivacy").RequiresPrivilege(Privilege.controlserver).HandleWith(_ => TextCommandResult.Success(GetStatus()));
        if (!config.Enabled) { status = "Disabled. Enable in NewfiesSeedPrivacy.json and restart; existing clients may already know the real seed."; return; }
        api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, StartPrivacy);
        if (api.Server.CurrentRunPhase >= EnumServerRunPhase.WorldReady) StartPrivacy();
    }

    private void StartPrivacy()
    {
        if (disposed || active || api == null) return;
        try
        {
            string version = typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion))?.GetRawConstantValue() as string ?? "unknown";
            if (version != "1.22.7" || api.World is not ServerMain server || !server.IsDedicatedServer) throw new NotSupportedException("Requires an official 1.22.7 dedicated server.");
            if (api.World.Seed == config.PublicSeed) throw new InvalidOperationException("PublicSeed must differ from the saved world seed.");
            var channel = api.ModLoader.GetModSystem<WeatherSystemServer>()?.serverChannel as NetworkChannel;
            if (channel == null) throw new InvalidOperationException("Native weather channel unavailable.");
            weatherChannel = (int)AccessTools.Field(typeof(NetworkChannelBase), "channelId").GetValue(channel)!;
            var types = (Dictionary<Type, int>)AccessTools.Field(typeof(NetworkChannelBase), "messageTypes").GetValue(channel)!;
            if (!types.TryGetValue(typeof(WeatherState), out weatherMessage)) throw new InvalidOperationException("Native weather message mapping unavailable.");
            var target = AccessTools.Method(typeof(ServerMain), "Serialize_", [typeof(Packet_Server)]);
            if (target?.ReturnType != typeof(int) || Harmony.GetPatchInfo(target)?.Owners.Count > 0) throw new InvalidOperationException("Native packet serializer is unavailable or already patched.");
            current = this;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SeedPrivacySystem), nameof(BeforeSerialize)) { priority = Priority.Last });
            active = true;
            status = "Active for native identification, weather messages and region weather data; client compatibility preview, restart required for changes.";
            api.Logger.Notification("[SeedPrivacy] {0} Real server generation and stored weather state remain unchanged.", status);
        }
        catch (Exception e)
        {
            harmony?.UnpatchAll(HarmonyId); harmony = null;
            if (current == this) current = null;
            status = "NOT ACTIVE: " + e.Message;
            api.Logger.Warning("[SeedPrivacy] {0}", status);
        }
    }

    private static void BeforeSerialize(ref Packet_Server p)
    {
        var owner = current;
        if (owner?.active != true) return;
        try { p = owner.Sanitize(p); }
        catch
        {
            Interlocked.Increment(ref owner.failures);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - Interlocked.Read(ref owner.lastError) >= 30000)
            {
                Interlocked.Exchange(ref owner.lastError, now);
                owner.api?.Logger.Warning("[SeedPrivacy] Rejected a seed-bearing packet that could not be safely sanitized. Original data was not serialized; inspect compatibility before continuing this preview.");
            }
            // Never return false with a stale reusable send buffer or send the raw
            // payload after a failure. Native callers retain their exception handling.
            throw new InvalidDataException("Seed privacy could not sanitize the outgoing packet.");
        }
    }

    internal Packet_Server Sanitize(Packet_Server packet)
    {
        if (packet.Id == 1 && packet.Identification != null)
        {
            var result = Copy(packet); result.Identification = Copy(packet.Identification);
            result.Identification.Seed = config.PublicSeed;
            Interlocked.Increment(ref identifications); return result;
        }
        if (packet.Id == 55 && packet.CustomPacket is { } custom && custom.ChannelId == weatherChannel && custom.MessageId == weatherMessage)
        {
            if (custom.Data == null) throw new InvalidDataException("Missing weather payload.");
            var result = Copy(packet); result.CustomPacket = Copy(custom);
            result.CustomPacket.SetData(SeedPrivacyWire.Weather(custom.Data, config.PublicSeed));
            Interlocked.Increment(ref weatherPackets); return result;
        }
        if (packet.Id == 42 && packet.MapRegion?.Moddata is { Length: > 0 } data)
        {
            var rewritten = SeedPrivacyWire.Region(data, config.PublicSeed);
            if (rewritten == null) return packet;
            var result = Copy(packet); result.MapRegion = Copy(packet.MapRegion);
            result.MapRegion.SetModdata(rewritten);
            Interlocked.Increment(ref regionPackets); return result;
        }
        return packet;
    }

    public string GetStatus() => $"Seed privacy: {status} Identifications={Interlocked.Read(ref identifications)}, weather={Interlocked.Read(ref weatherPackets)}, regions={Interlocked.Read(ref regionPackets)}, rejected={Interlocked.Read(ref failures)}. This covers inspected native paths, not arbitrary third-party mod channels or seed inference.";
    private static T Copy<T>(T original) where T : class => (T)CopyObject(original);
    public override void Dispose()
    {
        disposed = true; active = false;
        harmony?.UnpatchAll(HarmonyId); harmony = null;
        if (current == this) current = null;
        base.Dispose();
    }
}
