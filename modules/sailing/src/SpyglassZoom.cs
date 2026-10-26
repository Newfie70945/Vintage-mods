using System.Reflection;
using Vintagestory.API.Client;

namespace NewfiesSailing;

/// <summary>Client-side spyglass zoom: narrows the field of view while the item is held down and restores it afterwards.</summary>
public static class SpyglassZoom
{
    private static float? original;
    private static bool viaSettings;

    public static bool Active { get; private set; }

    public static void Begin(ICoreClientAPI capi, double fraction)
    {
        if (Active) return;
        try
        {
            PropertyInfo? prop = capi.Render.GetType().GetProperty("PerspectiveViewFov", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(float))
            {
                original = (float)prop.GetValue(capi.Render)!;
                prop.SetValue(capi.Render, (float)(original.Value * fraction));
                viaSettings = false;
            }
            else
            {
                original = capi.Settings.Int["fieldOfView"];
                capi.Settings.Int["fieldOfView"] = Math.Max(10, (int)(original.Value * fraction));
                viaSettings = true;
            }
            Active = true;
        }
        catch (Exception ex) { capi.Logger.Warning("[NewfiesSailing] Spyglass zoom failed: " + ex.Message); original = null; }
    }

    public static void End(ICoreClientAPI capi)
    {
        if (!Active) return;
        Active = false;
        try
        {
            if (original == null) return;
            if (viaSettings) capi.Settings.Int["fieldOfView"] = (int)original.Value;
            else capi.Render.GetType().GetProperty("PerspectiveViewFov", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(capi.Render, original.Value);
        }
        catch (Exception ex) { capi.Logger.Warning("[NewfiesSailing] Spyglass restore failed: " + ex.Message); }
        original = null;
    }
}
