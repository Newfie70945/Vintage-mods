using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// Realism for sailing (server physics, sail trim, squalls and warnings; client storm visuals).
/// </summary>
public sealed partial class SailingModSystem : ModSystem
{
    public const string ChannelName = "newfiessailing";

    public const string TrimKey = "newfiessailing:trim";

    public const string AnchorKey = "newfiessailing:anchor";

    public const string HullKey = "newfiessailing:hull";

    public const string CapsizedKey = "newfiessailing:capsized";

    public const string WaterKey = "newfiessailing:water";

    public const string SalvageKey = "newfiessailing:salvage";

    public const string WaveKey = "newfiessailing:wave";

    public const string MoraleKey = "newfiessailing:morale";

    public const string NameKey = "newfiessailing:name";

    public const string UpgradeKey = "newfiessailing:upgrades";

    public const string SailKey = "newfiessailing:sail";

    private const string LogPrefix = "newfiessailing:log:";

    private ICoreServerAPI? sapi;

    private IServerNetworkChannel? serverChannel;

    private SailingConfig config = new();

    private readonly Dictionary<long, Entity> boats = new();

    private readonly Dictionary<string, (int Force, long At)> lastWarn = new();

    private readonly Dictionary<long, long> lastOverloadWarn = new();

    private readonly Dictionary<long, double> smoothed = new();

    private SailingVisuals? visuals;

    private long tickListener;

    private double elapsed;

    private readonly Random rng = new();

    // Squall schedule in seconds of mod time.
    private double nextSquallAt;

    private double squallStart = -1, squallDuration;

    private bool squallWarned;

    private double squallFactor = 1;

    private double lastSyncAt = -10;

    // Temporal storm state (read from the game by reflection so the mod has no compile-time dependency on VSSurvivalMod).
    private bool temporalStorm;

    private bool forcedStorm;

    private double lastStormCheck = -10;

    private ModSystem? stabilitySystem;

    private bool stabilityLookedUp, stabilityLogged;

    private readonly List<long> boarders = new();

    private double lastBoarderAt = -1000;

    private double seasonFactor = 1;

    private double nextFogAt, fogStart = -1, fogDuration, fogIntensity;

    private bool fogAnnounced;

    private readonly List<long> wrecks = new();

    private readonly List<long> bottles = new();

    private readonly List<long> whirlpools = new();

    private double lastWhirlpoolAt = -1000;

    private readonly Dictionary<long, long> lastWhirlWarn = new();

    private readonly Dictionary<long, int> prevSide = new();

    private readonly Dictionary<long, double> tackPenalty = new();

    private readonly Dictionary<long, long> lastGybeWarn = new();

    private readonly Dictionary<long, long> lastGroundWarn = new();

    private readonly Dictionary<long, (double Factor, double At)> shadowCache = new();

    private readonly Dictionary<long, long> tows = new(); // towed boat id -> tower boat id
    private readonly Dictionary<string, long> pendingTow = new(); // player uid -> boat id to be towed
    private double lastTowParticles;

    private double nextBottleAt = 300;

    private double lastWreckAt = -1000;

    private readonly Dictionary<long, double> lastShanty = new();

    private readonly Dictionary<string, double> lastHorn = new();

    private string seasonName = "";

    private readonly Dictionary<string, Vec3d> lastPlayerPos = new();

    /// <summary>Spyglass zoom fraction from the client config (set by the client side on start).</summary>
    public double SpyglassFraction { get; private set; } = 0.22;

    public override void Start(ICoreAPI api)
    {
        api.RegisterItemClass("ItemSailingTool", typeof(ItemSailingTool));
        api.RegisterEntity("EntityRecoveryBalloon", typeof(EntityRecoveryBalloon));
        api.RegisterEntity("EntityDriftingWreck", typeof(EntityDriftingWreck));
        api.RegisterEntity("EntityDriftingBottle", typeof(EntityDriftingBottle));
        api.RegisterEntity("EntityWhirlpool", typeof(EntityWhirlpool));
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<SailingSyncPacket>();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        visuals = new SailingVisuals(api);
        SpyglassFraction = visuals.SpyglassFraction;
        api.Network.GetChannel(ChannelName).SetMessageHandler<SailingSyncPacket>(p => visuals.ApplySync(p));
        visuals.Start();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network.GetChannel(ChannelName);
        try { config = api.LoadModConfig<SailingConfig>("NewfiesSailing.json") ?? new SailingConfig(); }
        catch (Exception ex) { api.Logger.Warning("[NewfiesSailing] Bad config, using defaults: " + ex.Message); }
        api.StoreModConfig(config, "NewfiesSailing.json");
        ScheduleNextSquall();
        ScheduleNextFog();

        api.Event.OnEntitySpawn += Track;
        api.Event.OnEntityLoaded += Track;
        api.Event.OnEntityDespawn += (e, _) => Forget(e.EntityId);

        // Everything player-facing is a crafted item (see ItemSailingTool). Only an admin test switch remains a command.
        api.ChatCommands.Create("sailingadmin")
            .WithDescription("Admin: toggle a simulated temporal storm at sea (testing)")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(args =>
            {
                forcedStorm = !forcedStorm;
                return TextCommandResult.Success("Simulated temporal storm " + (forcedStorm ? "ON" : "OFF") + ".");
            });

        tickListener = api.Event.RegisterGameTickListener(OnTick, Math.Max(50, config.TickMilliseconds));
    }

    public override void Dispose()
    {
        visuals?.Stop();
        if (sapi != null && tickListener != 0) sapi.Event.UnregisterGameTickListener(tickListener);
        boats.Clear();
        base.Dispose();
    }

    private static readonly (string Code, int Min, int Max, double Chance)[] TreasureLoot =
    {
        ("game:ingot-copper", 2, 4, 0.9), ("game:ingot-iron", 1, 3, 0.6), ("game:ingot-tin", 1, 3, 0.5),
        ("game:ingot-silver", 1, 2, 0.4), ("game:ingot-gold", 1, 2, 0.25), ("game:gear-rusty", 1, 3, 0.7), ("game:flaxtwine", 2, 4, 0.6)
    };
}
