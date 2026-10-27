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
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_NOCLIP","noclipEnforcementEligible=True;correctionConfigured=True"),"Even legacy enforcement-enabled noclip cannot automatically punish");
Console.WriteLine("PASS: noclip review-mode and legacy findings cannot enter automatic punishment paths.");

// Mount/authorized-flight eligibility must discard accumulated hover evidence.
var exemptFlight = new PacketFlightReviewWindow();
for (int i=0;i<24;i++) Check(!exemptFlight.Observe(i*250,100,true,6000,1000), "Pre-exemption hover has not confirmed");
Check(!exemptFlight.Observe(6000,100,false,6000,1000) && exemptFlight.DurationMs==0, "Mounted or authorized flight clears pending evidence");
for (int i=0;i<24;i++) Check(!exemptFlight.Observe(6250+i*250,100,true,6000,1000), "After exemption a new full confirmation window is required");
Check(exemptFlight.Observe(12250,100,true,6000,1000), "Sustained eligible flight is still detectable after exemption");

Check(!PacketIngressPolicy.IsValidInventoryId(null) && !PacketIngressPolicy.IsValidInventoryId("") && !PacketIngressPolicy.IsValidInventoryId(new string('x',513)), "Malformed inventory IDs rejected");
Check(PacketIngressPolicy.IsValidInventoryId("ground-player") && PacketIngressPolicy.IsValidInventoryId("custom:tabbed-container"), "Native ground and modded inventory IDs preserved");
Check(PacketIngressPolicy.IsValidEntityInteraction(8192,1000,-25,0), "Large creature hit offsets remain valid");
Check(!PacketIngressPolicy.IsValidEntityInteraction(-1,0,0,0) && !PacketIngressPolicy.IsValidEntityInteraction(8193,0,0,0), "Entity selection bounds enforced");
foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,double.MaxValue})
    Check(!PacketIngressPolicy.IsValidEntityInteraction(0,invalid,0,0), "Malformed entity hit rejected");
Console.WriteLine("PASS: bounded inventory IDs and entity-hit numerical invariants, including large modded creatures.");

var terrainEvidence=new NoclipTerrainEvidence();
terrainEvidence.Add(1,2,3,"game:stone",false);terrainEvidence.Add(1,2,3,"game:stone",false);terrainEvidence.Add(2,2,3,"game:stone",false);
Check(terrainEvidence.Describe().Contains("uniqueIntersectedBlocks=2;") && terrainEvidence.Describe().Contains("uniqueBlockTypes=1;"),"Repeated samples distinguish block positions from types");
Check(terrainEvidence.CorrectionCompatible,"Ordinary complete solid terrain can support repeated correction evidence");
terrainEvidence.Add(3,2,3,"game:door",true);Check(!terrainEvidence.CorrectionCompatible,"Dynamic geometry cannot support corrections");
var cappedTerrain=new NoclipTerrainEvidence();for(int i=0;i<130;i++)cappedTerrain.Add(i,0,0,"game:stone",false);
Check(cappedTerrain.Truncated && !cappedTerrain.CorrectionCompatible,"Truncated terrain evidence cannot support corrections");
var confirmation=new NoclipConfirmationWindow();
Check(!confirmation.Observe(1000),"First noclip cannot correct");Check(!confirmation.Observe(1100),"Repeated same burst cannot correct");
Check(!confirmation.Observe(1750),"Second noclip cannot correct");Check(confirmation.Observe(2500),"Three spaced observations over 1.5s can support configured correction");
confirmation.Reset();Check(!confirmation.Observe(3000),"Transitions clear noclip confirmation");Check(!confirmation.Observe(10000),"Old observations cannot confirm a new episode");
Console.WriteLine("PASS: no noclip automatic punishment; distinct block evidence, complex geometry exclusions, bounded retention and repeated correction confirmation.");

var corrections=new NoclipCorrectionHistory();
Check(!corrections.Record(1000,5,120,10),"First correction never kicks");
Check(!corrections.Record(1100,5,120,10) && corrections.Count==1,"Burst corrections count once");
for(int i=1;i<4;i++)Check(!corrections.Record(1000+i*10000,5,120,10),"Corrections below threshold do not kick");
Check(corrections.Record(41000,5,120,10),"Fifth spaced correction can kick");
Check(!corrections.Record(162000,5,120,10) && corrections.Count==1,"Expired corrections decay independently");
Check(!corrections.Record(1000,5,120,10) && corrections.Count==1,"Clock regression discards old corrections");
Check(AntiCheatObservationPolicy.IsReviewOnly("PACKET_NOCLIP_CORRECTION",""),"Correction audit cannot trigger generic first-finding punishment");
Console.WriteLine("PASS: correction-first noclip escalation, minimum spacing, independent expiry and clock reset.");

var nuker=new BlockNukerWindow();
for(int i=0;i<30;i++)Check(nuker.Observe(1000,i,0,0,30,true).Allow,"First 30 distinct requests allowed");
var burst=nuker.Observe(1000,30,0,0,30,true);Check(!burst.Allow&&!burst.Kick,"31st denied but first burst cannot kick");
Check(!nuker.Observe(1100,31,0,0,30,true).Kick,"Same drained burst cannot kick");
for(int i=0;i<30;i++)nuker.Observe(2100,i,0,0,30,true);
Check(nuker.Observe(2100,30,0,0,30,true).Kick,"Repeated independently spaced burst can kick");
var lagged=new BlockNukerWindow();for(int i=0;i<100;i++)Check(!lagged.Observe(1000,i,0,0,30,false).Kick,"Lag-drained packets cannot kick");
var repeated=new BlockNukerWindow();for(int i=0;i<100;i++)Check(repeated.Observe(1000,1,2,3,30,true).Allow,"Duplicate targets are not distinct blocks");
Check(nuker.Observe(30000,100,0,0,30,true).Allow,"Old window expires");
Console.WriteLine("PASS: Block Nuker distinct-target cap, denial, independent burst confirmation, lag safety and duplicate exclusion.");

var pendingCorrection=new PendingNoclipCorrection();
pendingCorrection.Stage(1000,1,0,10,20,30);
Check(!pendingCorrection.Observe(1000,1,0,10,20,30,true),"Teleport request cannot instantly count as arrival");
Check(!pendingCorrection.Observe(1100,1,0,50,20,30,true)&&pendingCorrection.Active,"Wrong destination does not count");
Check(pendingCorrection.Observe(1200,1,0,10.1,20,30,true)&&!pendingCorrection.Active,"Observed destination counts once");
Check(!pendingCorrection.Observe(1300,1,0,10,20,30,true),"Same arrival cannot count twice");
pendingCorrection.Stage(1000,1,0,10,20,30);Check(!pendingCorrection.Observe(4100,1,0,10,20,30,true)&&!pendingCorrection.Active,"Late arrival cannot punish");
pendingCorrection.Stage(1000,1,0,10,20,30);Check(!pendingCorrection.Observe(1200,2,0,10,20,30,true),"Different entity cannot confirm correction");
pendingCorrection.Stage(1000,1,0,10,20,30);Check(!pendingCorrection.Observe(1200,1,1,10,20,30,true),"Different dimension cannot confirm correction");
pendingCorrection.Stage(1000,1,0,10,20,30);Check(!pendingCorrection.Observe(1200,1,0,10,20,30,false),"Lag/mount/disabled protection cancels correction confirmation");
nuker.Reset();for(int i=0;i<31;i++)Check(!nuker.Observe(40000,i,0,0,30,true).Kick,"Reset context cannot inherit old burst confirmation");
Console.WriteLine("PASS: asynchronous correction arrival, timeout, identity/dimension/health exclusions and nuker context reset.");

ServerProtectionExemptions.Clear();
Check(!ServerProtectionExemptions.TryGrant("u",ServerProtectionScope.Movement,3601,"test","admin",1000),"Exemptions have a hard expiry bound");
Check(ServerProtectionExemptions.TryGrant("u",ServerProtectionScope.Movement,60,"known launch mod","admin",1000),"Scoped temporary exemption grants");
Check(ServerProtectionExemptions.Has("u",ServerProtectionScope.Movement,2000)&&!ServerProtectionExemptions.Has("u",ServerProtectionScope.Mining,2000),"Movement exemption cannot exempt mining");
Check(!ServerProtectionExemptions.Has("u",ServerProtectionScope.Movement,61000),"Exemption expires");
Check(!ServerProtectionExemptions.TryGrant("u",(ServerProtectionScope)3,60,"test","admin",1000),"Combined scopes require separate explicit grants");
var recovery=new CorrectionRecoveryGuard();Check(!recovery.Fail(1000)&&!recovery.Fail(2000)&&recovery.Fail(3000)&&recovery.Suspended(4000),"Three failed corrections suspend recovery");
Check(!recovery.Suspended(63000),"Recovery pause expires");recovery.Success();Check(!recovery.Fail(64000),"Successful correction resets failure count");
var actions=new ActionRateWindow();for(int i=0;i<300;i++)actions.Observe(1000);Check(actions.Observe(1000)==256,"Action history remains bounded");Check(actions.Observe(2000)==1,"Old action observations expire");Check(actions.Observe(500)==1,"Clock regression resets rate evidence");
Check(AntiCheatObservationPolicy.IsReviewOnly("SERVER_ACTION_COMBAT_WALL","decision=denied")&&AntiCheatObservationPolicy.IsReviewOnly("PACKET_CORRECTION_SUSPENDED",""),"New actions and recovery alerts never trigger automatic kicks");
Console.WriteLine("PASS: scoped exemption bounds/expiry, recovery-loop guard, action history caps and advisory new-action policy.");

var miningRequest=new MiningRequestObservation();miningRequest.Start(1000,1,2,3,4,"mod:pick");miningRequest.Start(1500,1,2,3,4,"mod:pick");
Check(miningRequest.Elapsed(2000,1,2,3,4,"mod:pick")==1000,"Repeated request markers preserve first timestamp");
Check(miningRequest.Elapsed(2000,1,2,3,4,"other:pick")==null&&miningRequest.Elapsed(32000,1,2,3,4,"mod:pick")==null,"Changed tools and stale starts cannot corroborate mining");
Console.WriteLine("PASS: bounded mining-request evidence preserves server timing without trusting client damage amounts.");
