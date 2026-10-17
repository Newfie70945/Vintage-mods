using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace NewfiesPlayerList;

/// <summary>Optional renderer for Server Suite's privilege-gated supporter lamp.</summary>
public sealed class SupporterLampClient : IDisposable
{
    public const string ChannelName = "newfies-supporter-lamp-v1";
    private readonly ICoreClientAPI api;
    private readonly Dictionary<string, byte[]> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppliedLight> applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> stale = new();
    private IClientNetworkChannel? channel;
    private long tickId = -1;
    private long helloId = -1;
    private bool helloSent;
    private int helloAttempts;
    private bool disposed;
    private const int HelloMaxAttempts = 240;
    private const string WatchedEnabledKey = "nssSupporterLampEnabled";
    private const string WatchedHueKey = "nssSupporterLampHue";
    private const string WatchedSaturationKey = "nssSupporterLampSaturation";
    private const string WatchedBrightnessKey = "nssSupporterLampBrightness";
    private Harmony? harmony;
    private const string HarmonyId = "newfies.playerlist.supporterlamp";

    public SupporterLampClient(ICoreClientAPI api) => this.api = api;

    public void Register()
    {
        try
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(SupporterLampClient).Assembly);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[NewfiesPlayerList/SupporterLamp] Could not install LightHsv compatibility patch: {0}: {1}", ex.GetType().Name, ex.Message);
        }

        channel = api.Network.GetChannel(ChannelName)
            ?? api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<SupporterLampHelloPacket>()
                .RegisterMessageType<SupporterLampStatePacket>();
        channel.SetMessageHandler<SupporterLampStatePacket>(OnState);

        api.Event.LevelFinalize += OnLevelFinalize;
        tickId = api.Event.RegisterGameTickListener(_ => ApplyAll(), 100);
        StartHello();
        if (api.PlayerReadyFired) TryHello();
    }

    private void OnLevelFinalize()
    {
        foreach (string uid in new List<string>(active.Keys))
        {
            SupporterLampLightRegistry.Remove(uid);
            Apply(uid, null);
        }
        active.Clear();
        helloSent = false;
        helloAttempts = 0;
        StartHello();
        TryHello();
    }

    private void StartHello()
    {
        if (disposed || helloSent || helloId >= 0) return;
        helloAttempts = 0;
        helloId = api.Event.RegisterGameTickListener(_ =>
        {
            if (TryHello())
            {
                StopHello();
                return;
            }

            helloAttempts++;
            if (helloAttempts >= HelloMaxAttempts)
            {
                api.Logger.Warning("[NewfiesPlayerList/SupporterLamp] Suite lamp renderer handshake timed out after {0} attempts (last channel state: {1}). The renderer will retry after the next level finalize/reconnect.", helloAttempts, SafeState());
                StopHello();
            }
        }, 500);
    }

    private bool TryHello()
    {
        if (disposed || helloSent || channel == null || SafeState() != EnumChannelState.Connected) return false;
        try
        {
            channel.SendPacket(new SupporterLampHelloPacket { ProtocolVersion = 1 });
            helloSent = true;
            StopHello();
            return true;
        }
        catch { return false; }
    }

    private EnumChannelState SafeState()
    {
        try { return api.Network.GetChannelState(ChannelName); }
        catch { return EnumChannelState.NotFound; }
    }

    private void StopHello()
    {
        if (helloId < 0) return;
        api.Event.UnregisterGameTickListener(helloId);
        helloId = -1;
    }

    private void OnState(SupporterLampStatePacket packet)
    {
        if (disposed || packet == null || string.IsNullOrWhiteSpace(packet.PlayerUid)) return;
        if (!packet.Enabled)
        {
            active.Remove(packet.PlayerUid);
            SupporterLampLightRegistry.Remove(packet.PlayerUid);
            Apply(packet.PlayerUid, null);
            return;
        }

        var hsv = UpdateColor(packet.PlayerUid,
            (byte)Math.Clamp((int)packet.Hue, 0, 63),
            (byte)Math.Clamp((int)packet.Saturation, 0, 7),
            (byte)Math.Clamp((int)packet.Brightness, 0, 31));
        Apply(packet.PlayerUid, hsv);
    }

    private void ApplyAll()
    {
        if (disposed) return;
        SyncFromSuiteWatchedAttributes();
        foreach (var pair in active) Apply(pair.Key, pair.Value);
    }

    private void SyncFromSuiteWatchedAttributes()
    {
        try
        {
            var online = api.World?.AllOnlinePlayers;
            if (online == null) return;

            seen.Clear();
            foreach (var player in online)
            {
                if (player?.Entity == null || string.IsNullOrWhiteSpace(player.PlayerUID)) continue;
                string uid = player.PlayerUID;
                seen.Add(uid);

                bool enabled = player.Entity.WatchedAttributes?.GetBool(WatchedEnabledKey, false) == true;
                if (!enabled)
                {
                    if (active.Remove(uid))
                    {
                        SupporterLampLightRegistry.Remove(uid);
                        Apply(uid, null);
                    }
                    continue;
                }

                byte hue = (byte)Math.Clamp(player.Entity.WatchedAttributes?.GetInt(WatchedHueKey, 33) ?? 33, 0, 63);
                byte sat = (byte)Math.Clamp(player.Entity.WatchedAttributes?.GetInt(WatchedSaturationKey, 7) ?? 7, 0, 7);
                byte brightness = (byte)Math.Clamp(player.Entity.WatchedAttributes?.GetInt(WatchedBrightnessKey, 16) ?? 16, 0, 31);
                UpdateColor(uid, hue, sat, brightness);
            }

            stale.Clear();
            foreach (string uid in active.Keys)
            {
                if (!seen.Contains(uid)) stale.Add(uid);
            }
            foreach (string uid in stale)
            {
                active.Remove(uid);
                applied.Remove(uid);
                SupporterLampLightRegistry.Remove(uid);
            }
        }
        catch
        {
            // Packet state remains a fallback. Never make the player-list HUD depend on lamp sync.
        }
    }

    private byte[] UpdateColor(string uid, byte hue, byte saturation, byte brightness)
    {
        if (active.TryGetValue(uid, out var existing) && existing[0] == hue &&
            existing[1] == saturation && existing[2] == brightness) return existing;
        var hsv = new[] { hue, saturation, brightness };
        active[uid] = hsv;
        SupporterLampLightRegistry.Set(uid, hsv);
        return hsv;
    }

    private void Apply(string uid, byte[]? hsv)
    {
        try
        {
            var entity = string.Equals(api.World.Player?.PlayerUID, uid, StringComparison.OrdinalIgnoreCase)
                ? api.World.Player?.Entity
                : api.World.PlayerByUid(uid)?.Entity;
            if (hsv == null)
            {
                applied.Remove(uid);
                if (entity != null) entity.LightHsv = null;
            }
            else if (entity != null)
            {
                var next = new AppliedLight(entity.EntityId, hsv[0], hsv[1], hsv[2]);
                // The getter patch supplies the light continuously. Only write
                // when its color changes or a replacement player entity arrives.
                if (applied.TryGetValue(uid, out var previous) && previous == next) return;
                entity.LightHsv = (byte[])hsv.Clone();
                applied[uid] = next;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { api.Event.LevelFinalize -= OnLevelFinalize; } catch { }
        StopHello();
        if (tickId >= 0)
        {
            try { api.Event.UnregisterGameTickListener(tickId); } catch { }
            tickId = -1;
        }
        foreach (string uid in new List<string>(active.Keys))
        {
            SupporterLampLightRegistry.Remove(uid);
            Apply(uid, null);
        }
        active.Clear();
        applied.Clear();
        seen.Clear();
        stale.Clear();
        try { harmony?.UnpatchAll(HarmonyId); } catch { }
        harmony = null;
    }

    private readonly record struct AppliedLight(long EntityId, byte Hue, byte Saturation, byte Brightness);
}
