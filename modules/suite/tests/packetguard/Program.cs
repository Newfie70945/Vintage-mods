using BlockLogger.AntiCheat;

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
}

Check(PacketIngressPolicy.BoundViewDistanceBlocks(1_000_000, 1024, 12, 32) == 384, "Million-block request capped by server radius before scheduling");
Check(PacketIngressPolicy.BoundViewDistanceBlocks(int.MaxValue, 1024, int.MaxValue, 32) == 1024, "Radius multiplication cannot overflow");
Check(PacketIngressPolicy.BoundViewDistanceBlocks(-1, 1024, 12, 32) == 32, "Negative request normalized");
Check(PacketIngressPolicy.BoundViewDistanceBlocks(256, 1024, 12, 32) == 256, "Normal preference preserved");
Check(PacketIngressPolicy.BoundViewDistanceBlocks(2048, 512, 0, 32) == 512, "Hard resource cap works when server radius is unavailable");
foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
    Check(!PacketIngressPolicy.IsValidCoordinate(value), "Non-finite/out-of-address-space coordinate rejected");
Check(PacketIngressPolicy.IsValidCoordinate(123456.25), "Normal movement coordinate accepted");
Check(!PacketIngressPolicy.IsValidFace(-1) && !PacketIngressPolicy.IsValidFace(6) && PacketIngressPolicy.IsValidFace(5), "Face indexing bounds");
Check(!PacketIngressPolicy.IsValidHit(double.NaN) && !PacketIngressPolicy.IsValidHit(double.PositiveInfinity) && !PacketIngressPolicy.IsValidHit(double.NegativeInfinity) && !PacketIngressPolicy.IsValidHit(1000) && PacketIngressPolicy.IsValidHit(1.0005) && PacketIngressPolicy.IsValidHit(1.5) && PacketIngressPolicy.IsValidHit(2.5) && PacketIngressPolicy.IsValidHit(-0.5), "Hit coordinates bound without rejecting oversized modded selection boxes");
Check(!PacketIngressPolicy.IsValidBlockId(-1, 20) && !PacketIngressPolicy.IsValidBlockId(20, 20) && PacketIngressPolicy.IsValidBlockId(0, 20), "Registry ID bounds");

var budget = new PacketIngressBudget();
for (int i = 0; i < 8; i++) Check(budget.TryConsume(1000, 2, 8), "Configured normal burst accepted");
Check(!budget.TryConsume(1000, 2, 8), "Burst exhausted without unbounded history");
Check(!budget.TryConsume(900, 2, 8), "Regressed clock creates no tokens");
Check(!budget.TryConsume(1499, 2, 8), "Fractional refill cannot admit full packet");
Check(budget.TryConsume(1500, 2, 8), "Monotonic refill permits sustained rate");
for (int i = 0; i < 8; i++) Check(budget.TryConsume(1_000_000, 2, 8), "Long idle refill bounded to burst");
Check(!budget.TryConsume(1_000_000, 2, 8), "Idle interval does not create unbounded credit");
Console.WriteLine("PASS: production packet invariants, million-distance/overflow bounds, malformed coordinates/faces/hits/IDs, and token-bucket burst/refill/regressed-clock cases.");

var hover = new PacketFlightReviewWindow();
int observations = 0;
for (int tick = 0; tick <= 48; tick++) if (hover.Observe(tick * 250, 100, true, 6000, 1000)) observations++;
Check(observations == 1 && hover.DurationMs == 12000, "Continuous unsupported hover yields one advisory after six seconds");
hover.Observe(12250, 100, false, 6000, 1000);
Check(hover.DurationMs == 0, "Ground/liquid/climb/mount/unknown-terrain exclusions reset confirmation");
var falling = new PacketFlightReviewWindow();
for (int tick = 0; tick <= 60; tick++) Check(!falling.Observe(tick * 250, 100 - tick * 0.2, true, 6000, 1000), "Ordinary sustained falling never confirms flight");
var jump = new PacketFlightReviewWindow();
for (int tick = 0; tick < 8; tick++) Check(!jump.Observe(tick * 250, 100 + Math.Sin(tick * Math.PI / 7), true, 6000, 1000), "Normal short jump/apex cannot confirm");
jump.Observe(2000, 100, false, 6000, 1000);
var gap = new PacketFlightReviewWindow();
for (int tick = 0; tick < 20; tick++) Check(!gap.Observe(tick * 250, 100, true, 6000, 1000), "Initial incomplete window");
Check(!gap.Observe(10000, 100, true, 6000, 1000) && gap.DurationMs == 0, "Sampling gap discards incomplete evidence");
Check(!gap.Observe(9990, 100, true, 6000, 1000) && gap.DurationMs == 0, "Clock regression discards incomplete evidence");
Check(!gap.Observe(10240, double.NaN, true, 6000, 1000), "Invalid height cannot become flight evidence");
var rising = new PacketFlightReviewWindow();
for (int tick = 0; tick < 24; tick++) Check(!rising.Observe(tick * 250, 100 + tick * 0.1, true, 6000, 1000), "Ascending confirmation cannot occur early");
Check(rising.Observe(6000, 102.4, true, 6000, 1000) && rising.Rise > 2, "Sustained unsupported ascent yields advisory");
Console.WriteLine("PASS: production flight-review hover/ascent confirmation, ordinary falling/jump exclusions, one-review-per-airtime interval, invalid height and sampler/clock-gap resets.");
