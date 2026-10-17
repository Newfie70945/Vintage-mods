using System;
using System.Collections.Concurrent;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace NewfiesPlayerList;

internal static class SupporterLampLightRegistry
{
    private static readonly ConcurrentDictionary<string, byte[]> Active = new(StringComparer.OrdinalIgnoreCase);

    public static void Set(string uid, byte[] hsv)
    {
        if (string.IsNullOrWhiteSpace(uid) || hsv == null || hsv.Length < 3) return;
        Active[uid] = new[] { hsv[0], hsv[1], hsv[2] };
    }

    public static void Remove(string uid)
    {
        if (!string.IsNullOrWhiteSpace(uid)) Active.TryRemove(uid, out _);
    }

    public static bool TryGet(string uid, out byte[] hsv)
    {
        if (!string.IsNullOrWhiteSpace(uid) && Active.TryGetValue(uid, out var value))
        {
            hsv = value;
            return true;
        }
        hsv = Array.Empty<byte>();
        return false;
    }
}

/// <summary>
/// Merge the supporter lamp into the final player light value. This runs late so the
/// lamp coexists with vanilla hand lights and wearable-light mods that also patch
/// EntityPlayer.LightHsv.
/// </summary>
[HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.LightHsv), MethodType.Getter)]
internal static class SupporterLampLightPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(EntityPlayer __instance, ref byte[]? __result)
    {
        try
        {
            if (__instance == null || !SupporterLampLightRegistry.TryGet(__instance.PlayerUID, out var lamp)) return;
            __result = MergeLightHsv(__result, lamp);
        }
        catch
        {
            // Lighting is cosmetic. Never let another lighting mod's patch chain crash the client.
        }
    }

    // Vintage Story 1.22.7 reference sets do not all expose ColorUtil.MergeLightHSV.
    // Keep the same brightness-weighted merge locally so the supporter lamp still
    // blends with held/wearable lights without depending on that API helper.
    private static byte[]? MergeLightHsv(byte[]? rightHsv, byte[]? leftHsv)
    {
        if (leftHsv == null || leftHsv.Length < 3) return rightHsv;
        if (rightHsv == null || rightHsv.Length < 3) return leftHsv;

        float totalValue = rightHsv[2] + leftHsv[2];
        if (totalValue <= 0f)
        {
            return new byte[] { rightHsv[0], rightHsv[1], 0 };
        }

        float t = leftHsv[2] / totalValue;
        return new byte[]
        {
            (byte)(leftHsv[0] * t + rightHsv[0] * (1f - t)),
            (byte)(leftHsv[1] * t + rightHsv[1] * (1f - t)),
            Math.Max(leftHsv[2], rightHsv[2])
        };
    }
}
