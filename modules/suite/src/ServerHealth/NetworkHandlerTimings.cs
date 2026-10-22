using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;

namespace BlockLogger.ServerHealth;

public sealed class SlowNetworkHandler
{
    public long Sequence { get; set; }
    public long StartUnixMilliseconds { get; set; }
    public long EndUnixMilliseconds { get; set; }
    public string Channel { get; set; } = "";
    public int MessageId { get; set; }
    public string MessageType { get; set; } = "";
    public string Handler { get; set; } = "";
    public string Assembly { get; set; } = "";
    public int PayloadBytes { get; set; }
    public double WallMs { get; set; }
    public double GcPauseMs { get; set; }
    public string Error { get; set; } = "";
}

// Read-only timings around the official custom-packet dispatcher. No payload,
// player identity, packet filtering or handler replacement is performed.
internal sealed class NetworkHandlerTimings : IDisposable
{
    private const string PatchId = "newfies.suite.network-handler-timings";
    private static NetworkHandlerTimings? active;
    private readonly object gate = new();
    private readonly Queue<SlowNetworkHandler> events = new();
    private readonly Dictionary<(Vintagestory.Server.NetworkChannel, int), Identity> identities = new();
    private Harmony? harmony;
    private bool enabled;
    private int generation;
    private long sequence;
    private string status = "Disabled";
    private static readonly FieldInfo? Handlers = AccessTools.Field(typeof(Vintagestory.Server.NetworkChannel), "handlers");
    private static readonly FieldInfo? MessageTypes = AccessTools.Field(typeof(Vintagestory.Common.NetworkChannelBase), "messageTypes");
    internal string Status { get { lock (gate) return status; } }

    internal readonly record struct CallState(NetworkHandlerTimings? Owner, long Started, double GcPause, int Generation);
    private sealed record Identity(Delegate? Registered, string Message, string Handler, string Assembly);

    internal void Start()
    {
        lock (gate)
        {
            if (enabled) return;
            try
            {
                if (Volatile.Read(ref active) is { } other && !ReferenceEquals(other, this)) throw new InvalidOperationException("Another collector is active");
                var method = AccessTools.Method(typeof(Vintagestory.Server.NetworkChannel), "OnPacket", [typeof(Packet_CustomPacket), typeof(IServerPlayer)])
                    ?? throw new MissingMethodException("Official custom-packet dispatcher unavailable");
                harmony ??= new Harmony(PatchId);
                if (Harmony.GetPatchInfo(method)?.Owners.Contains(PatchId) != true)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(NetworkHandlerTimings), nameof(Before)),
                        finalizer: new HarmonyMethod(typeof(NetworkHandlerTimings), nameof(After)));
                generation++; enabled = true;
                status = "Active: custom handler intervals >=25ms; bounded to 256 recent records / 120 seconds";
                Volatile.Write(ref active, this);
            }
            catch (Exception e) { status = "Unavailable: " + e.GetType().Name; enabled = false; }
        }
    }

    internal void Stop()
    {
        lock (gate)
        {
            enabled = false; generation++;
            if (ReferenceEquals(Volatile.Read(ref active), this)) Volatile.Write(ref active, null);
            events.Clear(); identities.Clear(); status = "Disabled";
        }
    }

    private static void Before(out CallState __state)
    {
        var owner = Volatile.Read(ref active);
        __state = owner == null ? default : new(owner, Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration().TotalMilliseconds, Volatile.Read(ref owner.generation));
    }

    private static void After(Vintagestory.Server.NetworkChannel __instance, Packet_CustomPacket __0, CallState __state, Exception? __exception)
    {
        if (__state.Owner == null) return;
        double wall = Stopwatch.GetElapsedTime(__state.Started).TotalMilliseconds;
        if (wall < 25) return;
        // Diagnostics must never suppress or replace an exception from a handler.
        try { __state.Owner.Record(__instance, __0, __state, wall, __exception); }
        catch { __state.Owner.RecordFailure(); }
    }

    private void RecordFailure()
    {
        lock (gate) if (enabled) status = "Active with a dropped timing record; some handler evidence is unavailable";
    }

    private void Record(Vintagestory.Server.NetworkChannel channel, Packet_CustomPacket packet, CallState state, double wall, Exception? error)
    {
        long end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double pause = Math.Max(0, GC.GetTotalPauseDuration().TotalMilliseconds - state.GcPause);
        lock (gate)
        {
            if (!enabled || state.Generation != generation || packet == null) return;
            var identity = Resolve(channel, packet.MessageId);
            events.Enqueue(new SlowNetworkHandler {
                Sequence = ++sequence, StartUnixMilliseconds = end - (long)Math.Ceiling(wall), EndUnixMilliseconds = end,
                Channel = Short(channel.ChannelName), MessageId = packet.MessageId, MessageType = identity.Message,
                Handler = identity.Handler, Assembly = identity.Assembly, PayloadBytes = packet.Data?.Length ?? 0,
                WallMs = wall, GcPauseMs = pause, Error = error?.GetType().Name ?? ""
            });
            while (events.Count > 256 || (events.Count > 0 && events.Peek().EndUnixMilliseconds < end - 120_000)) events.Dequeue();
        }
    }

    private Identity Resolve(Vintagestory.Server.NetworkChannel channel, int message)
    {
        var handlers = Handlers?.GetValue(channel) as Delegate[];
        Delegate? registered = handlers != null && message >= 0 && message < handlers.Length ? handlers[message] : null;
        var key = (channel, message);
        if (identities.TryGetValue(key, out var cached) && ReferenceEquals(cached.Registered, registered)) return cached;
        string name = "Unknown message";
        if (MessageTypes?.GetValue(channel) is Dictionary<Type, int> types)
            foreach (var entry in types) if (entry.Value == message) { name = entry.Key.FullName ?? entry.Key.Name; break; }
        Delegate? actual = registered;
        // Official SetMessageHandler<T> stores a deserialization closure around
        // the mod delegate. Inspect only that known closure's delegate field.
        if (registered?.Method.DeclaringType?.Assembly == typeof(Vintagestory.Server.NetworkChannel).Assembly && registered.Target != null)
        {
            var field = registered.Target.GetType().GetField("handler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(registered.Target) is Delegate inner) actual = inner;
        }
        var identity = new Identity(registered, Short(name), Short(actual == null ? "No registered handler" : actual.Method.DeclaringType?.FullName + "." + actual.Method.Name),
            Short(actual?.Method.Module.Assembly.GetName().Name ?? "Unknown"));
        if (identities.Count < 512) identities[key] = identity;
        return identity;
    }

    internal List<SlowNetworkHandler> Match(long start, long end, double delay)
    {
        if (start <= 0 || end < start || !double.IsFinite(delay) || delay < 0) return new();
        long first = start - 1000 - (long)Math.Min(delay, 3_600_000);
        lock (gate)
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120_000;
            while (events.Count > 0 && events.Peek().EndUnixMilliseconds < cutoff) events.Dequeue();
            return events.Where(e => e.EndUnixMilliseconds >= first && e.StartUnixMilliseconds <= end)
                .OrderByDescending(e => e.WallMs).Take(8).ToList();
        }
    }
    private static string Short(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value[..Math.Min(value.Length, 240)];
    public void Dispose()
    {
        Stop();
        lock (gate)
        {
            if (Volatile.Read(ref active) == null) harmony?.UnpatchAll(PatchId);
            harmony = null;
        }
    }
}
