using BlockLogger.AntiCheat;
using NewfiesAntiCheat.Shared.Models;
using NewfiesAntiCheat.Shared.Packets;

static void Check(bool actual, bool expected, string message)
{
    if (actual != expected) throw new Exception(message);
}

Check(ClientReportBounds.IsValid(null), false, "null report rejected");
var report = new ClientScanReportPacket
{
    Mods = [new() { ModId = "newfiesclientintegrity", Version = "1.1.27", FileName = "companion.zip" }],
    Assemblies = [new() { AssemblyName = "NewfiesAntiCheatClient", AssociatedModIds = ["newfiesclientintegrity"] }],
    Findings = [new() { Code = "SAMPLE", Summary = "Legitimate bounded evidence", Evidence = new string('a', 4096) }]
};
Check(ClientReportBounds.IsValid(report), true, "ordinary companion report accepted");
report.Assemblies[0].AssociatedModIds = Enumerable.Repeat("mod", 33).ToList();
Check(ClientReportBounds.IsValid(report), false, "oversized nested association list rejected");
report.Assemblies[0].AssociatedModIds = [];
report.Assemblies[0].ModSystemTypeNames = [null!];
Check(ClientReportBounds.IsValid(report), false, "null nested entry rejected before hashing");
report.Assemblies[0].ModSystemTypeNames = [];
report.Mods = [null!];
Check(ClientReportBounds.IsValid(report), false, "null mod entry rejected");
report.Mods = [];
report.Findings[0].Evidence = new string('x', 16385);
Check(ClientReportBounds.IsValid(report), false, "unbounded finding evidence rejected");
report.Findings = Enumerable.Range(0, 256).Select(index => new ScanFindingDto { Evidence = new string('x', 16384) }).ToList();
Check(ClientReportBounds.IsValid(report), false, "aggregate character budget rejected");
report.Findings = [];
report.Harmony = new() { Patches = [new() { TargetMethod = new string('x', 2049) }] };
Check(ClientReportBounds.IsValid(report), false, "oversized Harmony metadata rejected");
report.Harmony = null;
report.Runtime = new() { NewAssemblies = [null!] };
Check(ClientReportBounds.IsValid(report), false, "null runtime assembly rejected");
Console.WriteLine("PASS: report shape, nested collections, null entries, evidence and aggregate budgets");
