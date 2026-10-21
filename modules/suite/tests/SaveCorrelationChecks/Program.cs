using BlockLogger.ServerHealth;
int count = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
var save = new WorldSaveLagWindow(10_000, 800);
Check(WorldSaveLagCorrelation.Match(new[] { save }, 11_000, 600) == save, "Overlapping save not matched");
Check(WorldSaveLagCorrelation.Match(new[] { save }, 20_000, 600) == null, "Unrelated save blamed");
Check(WorldSaveLagCorrelation.Match(new[] { save }, 9_000, 600) == null, "Future save blamed");
Check(WorldSaveLagCorrelation.Match(new[] { new WorldSaveLagWindow(10_000, 10) }, 11_000, 600) == null, "Tiny callback window blamed");
Check(WorldSaveLagCorrelation.Match(new[] { save }, 11_000, 20) == null, "Healthy interval classified as save lag");
Check(WorldSaveLagCorrelation.Match(Array.Empty<WorldSaveLagWindow>(), 11_000, 600) == null, "No observation fabricated");
Check(WorldSaveLagCorrelation.Match(new[] { save }, 11_000, double.NaN) == null, "Invalid delay accepted");
Check(WorldSaveLagCorrelation.Match(new[] { save }, 11_000, double.PositiveInfinity) == null, "Infinite delay accepted");
var longest = new WorldSaveLagWindow(9_500, 1500);
Check(WorldSaveLagCorrelation.Match(new[] { save, longest }, 11_000, 600) == longest, "Strongest overlapping window not selected");
Check(WorldSaveLagCorrelation.Match(new[] { new WorldSaveLagWindow(11_000, 250) }, 11_250, 250) != null, "Inclusive window endpoint lost");
var sixUnrelatedSaves = Enumerable.Range(0, 6).Select(i => new WorldSaveLagWindow(i * 600_000L, 800)).ToArray();
Check(Enumerable.Range(0, 6).All(i => WorldSaveLagCorrelation.Match(sixUnrelatedSaves, i * 600_000L + 300_000, 600) == null), "Equal counts mistaken for correlation");
Console.WriteLine($"PASS {count} save/lag interval correlation, unrelated counts, fast saves and invalid-input checks");
