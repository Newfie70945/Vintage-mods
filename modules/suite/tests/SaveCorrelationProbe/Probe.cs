using System.Reflection;
using System.Text.Json;
using BlockLogger;
using BlockLogger.ServerHealth;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

// Disposable test server only: the armed hook deliberately stalls a real save.
// Never package this probe in a production mod ZIP.
public sealed class SaveCorrelationProbe : ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        ServerHealthService Health()
        {
            var system = api.ModLoader.GetModSystem<BlockLoggerModSystem>();
            var module = (BlockLoggerModule)typeof(BlockLoggerModSystem).GetField("module", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(system)!;
            return module.ServerHealth!;
        }
        api.ChatCommands.Create("nsssaveprobe").RequiresPrivilege(Privilege.controlserver).HandleWith(_ =>
        {
            var health = Health();
            health.SetAutomaticIncidentProfilingEnabled(false);
            void StallOnce() { api.Event.GameWorldSave -= StallOnce; Thread.Sleep(1800); }
            api.Event.GameWorldSave += StallOnce;
            return TextCommandResult.Success("SAVE_PROBE_ARMED");
        });
        api.ChatCommands.Create("nsssaveprobereport").RequiresPrivilege(Privilege.controlserver).HandleWith(_ =>
        {
            var health = Health();
            var windows = (System.Collections.ICollection)typeof(ServerHealthService).GetField("worldSaveWindows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(health)!;
            var snapshot = health.GetSnapshot();
            api.Logger.Notification("SAVE_PROBE_REPORT {0}", JsonSerializer.Serialize(new { windows = windows.Count, samples = snapshot.Samples.Count, incidents = snapshot.DiagnosticIncidents, smart = snapshot.SmartAnalysis }));
            return TextCommandResult.Success("Probe report written.");
        });
    }
}
