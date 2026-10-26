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

var flood=new PacketFloodConfirmation();
for(int i=0;i<10000;i++)Check(!flood.Observe(1000),"Single delayed-tick burst cannot confirm flood");
for(int i=1;i<30;i++)Check(!flood.Observe(1000+i*100),"Sustained flood minimum duration enforced");
Check(flood.Observe(4000),"Repeated continuous over-budget traffic confirms after minimum span");
Check(!flood.Observe(4100,false),"Unreliable server timing invalidates confirmation");
for(int i=0;i<30;i++)Check(!flood.Observe(4200+i*100),"Recovery needs fresh healthy confirmation span");
Check(flood.Observe(7200),"Healthy sustained traffic can confirm again");
Check(!flood.Observe(10000),"Quiet gap resets confirmation");
Check(!flood.Observe(9000),"Clock regression resets confirmation");
var separate=new PacketFloodConfirmation();Check(!separate.Observe(10000),"Different budgets cannot prime one another");
Check(!PacketReachEvidence.Eligible("use",100,100,10000,3,2,true),"Right-click compatibility observations cannot punish");
Check(!PacketReachEvidence.Eligible("break",1,1,1000,3,2,true),"A single hard excess can be rejected but cannot punish");
Check(!PacketReachEvidence.Eligible("break",3,1,1000,3,2,true),"One held target cannot manufacture independent targets");
Check(!PacketReachEvidence.Eligible("break",3,2,0,3,2,true),"One queued tick cannot confirm reach");
Check(!PacketReachEvidence.Eligible("break",3,2,1000,3,2,false),"Server lag cannot confirm reach punishment");
Check(PacketReachEvidence.Eligible("break",3,2,1000,3,2,true),"Repeated healthy break reach remains enforceable");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_FLOOD","budget=blocks;floodConfirmed=False"),"Short flood observation review-only");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_FLOOD",null),"Legacy unconfirmed flood review-only");
Check(!AntiCheatObservationPolicy.IsReviewOnly("PACKET_FLOOD","floodConfirmed=True"),"Confirmed flood remains enforceable");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_FLOOD","xfloodConfirmed=True"),"Substring flag cannot confer confirmation");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_BLOCK_REACH","action=use;reachEnforcementEligible=True"),"Use callbacks cannot become punitive even with malformed old metadata");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_BLOCK_REACH","action=break;reachEnforcementEligible=True;timingRejectSuppressed=True"),"Critical timing blocks reach enforcement");
Check(!AntiCheatObservationPolicy.IsReviewOnly("PACKET_BLOCK_REACH","action=break;reachEnforcementEligible=True;timingRejectSuppressed=False"),"Confirmed break enforcement retained");
Check(!AntiCheatObservationPolicy.IsReviewOnly("PACKET_MALFORMED_PACKET","floodConfirmed=False"),"Protocol invariants do not become advisory");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_MINING_CADENCE_UNCERTAIN",""),"Uncertain mining is not punishment evidence");
Check(PacketTimingHealth.EffectiveTickMs(50,1000,50,4)==400,"Current stall is visible before next tick callback, bounded by configured multiplier");
Check(PacketTimingHealth.EffectiveTickMs(200,25,50,4)==200,"One fresh tick cannot erase smoothed lag");
Check(PacketTimingHealth.EffectiveTickMs(50,25,50,4)==50,"Ordinary intra-tick delay remains healthy");
Check(PacketReachGeometry.ValidBox(-1,0,0,4,1,1),"Oversized server selection geometry accepted");
Check(PacketReachGeometry.Distance(4,0.5,0.5,-1,0,0,4,1,1)==0,"Rendered protrusion can be in reach while anchor cube is distant");
Check(PacketReachGeometry.Distance(7,0.5,0.5,-1,0,0,4,1,1)==3,"True out-of-range geometry stays out of range");
Check(!PacketReachGeometry.ValidBox(double.NaN,0,0,1,1,1)&&!PacketReachGeometry.ValidBox(0,0,0,1000000,1,1),"Invalid/unbounded geometry cannot be used as enforcement proof");
Console.WriteLine("PASS: burst versus sustained flood, healthy-timing confirmation, distinct-target/span reach evidence, review-only consequence metadata, live stall timing and server selection geometry.");

Check(PacketReachEvidence.PingMilliseconds(double.NaN)==1000&&PacketReachEvidence.PingMilliseconds(double.PositiveInfinity)==1000&&PacketReachEvidence.PingMilliseconds(0.1)==100,"Unknown ping uses bounded reconciliation rather than NaN reach arithmetic");

// A large first burst must not supply all the evidence for later sparse overages.
var sparse=new PacketFloodConfirmation();
for(int i=0;i<10000;i++)Check(!sparse.Observe(1000),"Burst cannot add independent observation windows");
Check(!sparse.Observe(2000)&&!sparse.Observe(3000)&&!sparse.Observe(4000),"Sparse tail cannot inherit a burst's packet count");
Check(sparse.Evidence.Windows==4&&sparse.Evidence.SpanMs==3000,"Only independently timed observations counted");
for(int i=0;i<6;i++)sparse.Observe(4100+i*100);
Check(sparse.Evidence.Windows==10&&sparse.Observe(4700),"Sustained distributed overages still confirm");
Check(!sparse.Observe(4800,false)&&sparse.Evidence.Windows==0,"Lag clears accumulated observations");
var shortBurst=new PacketFloodConfirmation();
for(int i=0;i<=3500;i++)Check(!shortBurst.Observe(i<3000?1000:4000),"Separated bursts cannot confirm across a quiet gap");
Check(budget.Snapshot.Accepted==17&&budget.Snapshot.Dropped==4&&budget.Snapshot.Rate==2&&budget.Snapshot.Burst==8,"Budget counters match actual decisions without changing limits");
var culture=System.Globalization.CultureInfo.CurrentCulture;
try{System.Globalization.CultureInfo.CurrentCulture=new("fr-FR");Check(new PacketIngressBudgetSnapshot(3,2,0.5,60,180).Evidence.Contains("tokensRemaining=0.50"),"Evidence stays invariant across server locales");}finally{System.Globalization.CultureInfo.CurrentCulture=culture;}
Check(PacketReachEvidence.IsUsableSample(1000,900,350,0,0,12,12),"Recent same-entity/same-dimension sample retained");
Check(PacketReachEvidence.IsUsableSample(1000,650,350,0,0,12,12),"Rewind boundary inclusive");
Check(!PacketReachEvidence.IsUsableSample(1000,649,350,0,0,12,12),"Stale reach sample rejected");
Check(!PacketReachEvidence.IsUsableSample(1000,1001,350,0,0,12,12),"Future sample cannot authorize reach after clock regression");
Check(!PacketReachEvidence.IsUsableSample(1000,900,350,0,1,12,12),"Different dimensions cannot supply reach history");
Check(!PacketReachEvidence.IsUsableSample(1000,900,350,0,0,12,13),"Previous entity cannot supply reach history");
Check(!PacketReachEvidence.IsUsableSample(long.MaxValue,long.MinValue,350,0,0,12,12),"Invalid timestamp cannot wrap into the rewind window");
Console.WriteLine("PASS: independently spaced flood evidence, burst/sparse-tail isolation, unchanged token decisions, invariant budget telemetry, bounded history and clock/entity/dimension exclusions.");

var identityFlight=new PacketFlightReviewWindow();
for(int i=0;i<24;i++)Check(!identityFlight.Observe(i*250,100,true,6000,1000,entityId:1,dimension:0),"Flight review waits for sustained evidence");
Check(!identityFlight.Observe(6000,100,true,6000,1000,entityId:2,dimension:0)&&identityFlight.DurationMs==0,"New entity resets flight review");
Check(!identityFlight.Observe(6250,100,true,6000,1000,entityId:2,dimension:1)&&identityFlight.DurationMs==0,"New dimension resets flight review");
Check(!PacketMovementEvidence.FreshPosition(1000,1001)&&!PacketMovementEvidence.FreshPosition(10000,0),"Future/stale movement cannot support collision or flight accusations");
Check(!PacketMovementEvidence.ReliableCollisionTiming(true,false,false,false,true)&&!PacketMovementEvidence.ReliableCollisionTiming(true,true,true,false,true)&&!PacketMovementEvidence.ReliableCollisionTiming(true,true,false,true,true),"Warmup, degraded timing and critical lag suppress noclip evidence");
Check(PacketMovementEvidence.ReliableCollisionTiming(true,true,false,false,true)&&!PacketMovementEvidence.ReliableCollisionTiming(true,true,false,false,false),"Healthy fresh movement remains eligible");
Console.WriteLine("PASS: flight identity/dimension resets and fresh/healthy noclip evidence eligibility.");

Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_NOCLIP",""),"Old noclip observations without fresh eligibility cannot punish");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_NOCLIP","noclipEnforcementEligible=False;correctionConfigured=False"),"Noclip correction-off observations stay review-only");
Check(!AntiCheatObservationPolicy.IsReviewOnly("PACKET_NOCLIP","noclipEnforcementEligible=True;correctionConfigured=True"),"Qualified correction-enabled collision evidence retains configured enforcement");
Console.WriteLine("PASS: noclip review-mode and legacy findings cannot enter automatic punishment paths.");

// Mount/authorized-flight eligibility must discard accumulated hover evidence.
var exemptFlight = new PacketFlightReviewWindow();
for (int i=0;i<24;i++) Check(!exemptFlight.Observe(i*250,100,true,6000,1000), "Pre-exemption hover has not confirmed");
Check(!exemptFlight.Observe(6000,100,false,6000,1000) && exemptFlight.DurationMs==0, "Mounted or authorized flight clears pending evidence");
for (int i=0;i<24;i++) Check(!exemptFlight.Observe(6250+i*250,100,true,6000,1000), "After exemption a new full confirmation window is required");
Check(exemptFlight.Observe(12250,100,true,6000,1000), "Sustained eligible flight is still detectable after exemption");
