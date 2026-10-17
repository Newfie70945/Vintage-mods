using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlockLogger.Roles;
using BlockLogger.PlayerCommands;
using BlockLogger.WebConsole;
using BlockLogger.PlayerList;
using BlockLogger.WebDashboard;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.Common;
using Vintagestory.Server;
using Microsoft.Data.Sqlite;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string modPath=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!;
string discord=Environment.GetEnvironmentVariable("NSS_DISCORD_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(string root in new[]{Path.GetDirectoryName(modPath)!,Path.GetDirectoryName(discord)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string p=Path.Combine(root,name.Name+".dll");if(File.Exists(p))return context.LoadFromAssemblyPath(p);}return null;
};
Run(modPath,discord,args);
[MethodImpl(MethodImplOptions.NoInlining)] static void Run(string dll,string discord,string[] args){
 void Check(bool value,string message){if(!value)throw new Exception(message);}
 var mod=typeof(RoleManagementService).Assembly;
 var html=(string)mod.GetType("BlockLogger.WebDashboard.WebDashboardPage")!.GetField("Html")!.GetValue(null)!;
 Check(!html.Contains("__NSS_ROLE_"),"Role script placeholder not expanded");if(args.Length>0)File.WriteAllText(args[0],html);
 string root=Path.Combine(Path.GetTempPath(),"nss-admin-panel-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
 try {
  var fixture=new Fixture(root);var api=(ICoreServerAPI)fixture.Make(typeof(ICoreServerAPI))!;
  var policy=new PlayerCommandConfig();policy.HomeRoleLimits["other"]=7;
  var list=new PublicPlayerListServer(api,()=>null);list.Config.RoleNameColors["other"]="#123456";
  var service=new RoleManagementService(api,()=>policy,()=>list);
  RoleEditorRow Draft(string code)=>new(){Code=code,NameColorHex="#aabbcc",Name="Builder",Description="Can build",ColorHex="#778899",PrivilegeLevel=5,DefaultGameMode="Survival",LandClaimAllowance=5000,LandClaimMaxAreas=3,ClaimMinX=5,ClaimMinY=5,ClaimMinZ=5,Privileges=new(){"chat"},HomeLimit=4,Policies=new(){{"home",new(){Allowed=true,CooldownSeconds=0}}}};
  var initial=service.GetSnapshot();Check(initial.Available&&initial.Roles.Count==2,"Role snapshot lacks real engine roles");
  Check(service.Change(new(){Revision="stale",Action="create",Role=Draft("builder")}).Conflict,"Stale edits accepted");
  var created=service.Change(new(){Revision=initial.Revision,Action="create",Role=Draft("builder"),UseRoleNameColors=true});Check(created.Success,created.Message);
  Check(fixture.Config.RolesByCode.ContainsKey("builder")&&fixture.Config.Roles.Count==3,"Creation modified only interface copy");
  Check(policy.HomeRoleLimits["builder"]==4&&policy.HomeRoleLimits["other"]==7&&policy.TeleportRolePolicies["home"]["builder"].CooldownSeconds==0,"Shared policies not preserved");
  Check(list.Config.RoleNameColors["builder"]=="#AABBCC"&&list.Config.RoleNameColors["other"]=="#123456"&&list.Config.UseRoleNameColors,"Shared name colors not persisted/preserved");
  var role=fixture.Config.RolesByCode["builder"];var updated=Draft("builder");updated.Name="Master Builder";updated.HomeLimit=null;updated.NameColorHex="";updated.Policies.Clear();
  Check(service.Change(new(){Revision=created.Snapshot!.Revision,Role=updated}).Success,"Role update rejected");Check(ReferenceEquals(role,fixture.Config.RolesByCode["builder"])&&role.Name=="Master Builder","Engine role identity replaced");Check(!policy.HomeRoleLimits.ContainsKey("builder")&&!policy.TeleportRolePolicies.ContainsKey("home"),"Inheritance did not remove overrides");
  Check(!list.Config.RoleNameColors.ContainsKey("builder"),"Cleared color override remained");
  fixture.Manager.PlayerDataByUid["offline"]=new ServerPlayerData{PlayerUID="offline",LastKnownPlayername="Offline Alice",RoleCode="builder"};
  Check(!service.Change(new(){Revision=service.GetSnapshot().Revision,Action="delete",Role=Draft("builder")}).Success,"Assigned offline role removed");
  var assigned=service.SetPlayerRole(new(){PlayerUid="offline",ExpectedRoleCode="builder",RoleCode="default"});Check(assigned.Success&&fixture.Manager.playerDataDirty&&fixture.Manager.PlayerDataByUid["offline"].RoleCode=="default",assigned.Message);
  Check(service.SetPlayerRole(new(){PlayerUid="offline",ExpectedRoleCode="builder",RoleCode="admin"}).Conflict,"Stale membership accepted");
  Check(service.Change(new(){Revision=service.GetSnapshot().Revision,Action="delete",Role=Draft("builder")}).Success,"Unused role cannot be deleted");
  Check(!service.Change(new(){Revision=service.GetSnapshot().Revision,Action="delete",Role=Draft("default")}).Success,"Default role deleted");
  var admin=Draft("admin");admin.Privileges.Clear();Check(!service.Change(new(){Revision=service.GetSnapshot().Revision,Role=admin}).Success,"Last admin role stripped");
  JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
  var anti=new BlockLogger.AntiCheat.AntiCheatService(api,()=>null);
  var speedSession=new BlockLogger.AntiCheat.AntiCheatSession();
  var confirmed=typeof(BlockLogger.AntiCheat.AntiCheatSession).GetProperty("ConfirmedSpeed",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(speedSession)!;
  var observe=confirmed.GetType().GetMethod("Observe",BindingFlags.NonPublic|BindingFlags.Instance)!;
  long speedNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  foreach(long at in new[]{speedNow-20_000,speedNow-10_000,speedNow})observe.Invoke(confirmed,new object[]{at});
  var runtime=typeof(BlockLogger.AntiCheat.AntiCheatService).GetField("runtimeAvailable",BindingFlags.NonPublic|BindingFlags.Instance)!;
  var decide=typeof(BlockLogger.AntiCheat.AntiCheatService).GetMethod("BuildDecisionSnapshot",BindingFlags.NonPublic|BindingFlags.Instance)!;
  JsonElement Decision()=>Json(decide.Invoke(anti,new object[]{speedSession})!);
  anti.Config.Enabled=true;anti.Config.PacketGuardKickRepeatedConfirmedSpeed=true;anti.Config.PacketGuardConfirmedSpeedRepeatRisk=25;
  Check(!Decision().GetProperty("repeatedSpeedEligible").GetBoolean(),"Unavailable runtime advertised enforcement");
  runtime.SetValue(anti,true);Check(Decision().GetProperty("repeatedSpeedEligible").GetBoolean(),"Confirmed repeated-speed conditions missing");
  anti.Config.Enabled=false;Check(!Decision().GetProperty("repeatedSpeedEligible").GetBoolean()&&Decision().GetProperty("speedExplanation").GetString()!.Contains("inactive"),"Disabled anti-cheat advertised enforcement");
  anti.Config.Enabled=true;anti.Config.PacketGuardKickRepeatedConfirmedSpeed=false;Check(!Decision().GetProperty("repeatedSpeedEligible").GetBoolean(),"Disabled repeat-speed kick advertised eligibility");
  Console.WriteLine("PASS: decision explanations distinguish active enforcement, disabled module, unavailable runtime and disabled repeat-speed kicking.");
  var previewRow=Draft("default");previewRow.Policies["home"]=new(){Allowed=true,CooldownSeconds=-1};
  var previewSnapshot=service.GetSnapshot();previewSnapshot.DefaultCooldowns["home"]=30;previewSnapshot.WildcardPolicies["home"]=new(){Allowed=false,CooldownSeconds=900};
  var effective=Json(RoleManagementService.DescribePolicy(previewSnapshot,previewRow,new[]{"controlserver"},new[]{"controlserver"},9));
  Check(effective.GetProperty("homeLimit").GetInt32()==9&&effective.GetProperty("homeSource").GetString()=="Player override","Player home override missing from effective preview");
  var home=effective.GetProperty("commands").EnumerateArray().First(x=>x.GetProperty("command").GetString()=="home");
  Check(home.GetProperty("allowed").GetBoolean()&&home.GetProperty("cooldownSeconds").GetInt32()==30,"Role −1 cooldown must use command default, not wildcard cooldown");
  Check(!effective.GetProperty("commands").EnumerateArray().First(x=>x.GetProperty("command").GetString()=="t2p").GetProperty("allowed").GetBoolean(),"Direct controlserver denial bypassed default teleport access");
  var policyBefore=RoleManagementService.PolicyRevision(policy);
  Check(Json(service.PreviewDefinition(new(){Revision="old",Role=previewRow})).GetProperty("success").GetBoolean()==false,"Stale role preview accepted");
  Check(Json(service.PreviewDefinition(new(){Revision=service.GetSnapshot().Revision,Role=previewRow})).GetProperty("success").GetBoolean(),"Valid definition preview unavailable");
  Check(policyBefore==RoleManagementService.PolicyRevision(policy),"Preview mutated shared policies");
  var batch=new RoleBatchRequest{Revision=service.GetSnapshot().Revision,RoleCode="admin",Players=new(){new(){PlayerUid="offline",ExpectedRoleCode="default"}}};
  Check(Json(service.ChangeBatch(batch)).GetProperty("success").GetBoolean()&&fixture.Manager.PlayerDataByUid["offline"].RoleCode=="default","Batch preview changed membership");
  batch.Players[0].ExpectedRoleCode="stale";batch.Preview=false;
  Check(!Json(service.ChangeBatch(batch)).GetProperty("success").GetBoolean()&&fixture.Manager.PlayerDataByUid["offline"].RoleCode=="default","Stale batch changed membership");
  batch.Players[0].ExpectedRoleCode="default";Check(Json(service.ChangeBatch(batch)).GetProperty("applied").GetInt32()==1&&fixture.Manager.PlayerDataByUid["offline"].RoleCode=="admin","Previewed assignment not applied");
  Console.WriteLine("PASS: effective player overrides, direct denials and inherited cooldowns; read-only draft/bulk previews and conflict-safe assignment.");

  fixture.Manager.PlayerDataByUid["claims-a"]=new ServerPlayerData{PlayerUID="claims-a",LastKnownPlayername="Claims A",RoleCode="default",ExtraLandClaimAllowance=100,ExtraLandClaimAreas=1};
  fixture.Manager.PlayerDataByUid["claims-b"]=new ServerPlayerData{PlayerUID="claims-b",LastKnownPlayername="Claims B",RoleCode="default",ExtraLandClaimAllowance=7,ExtraLandClaimAreas=2};
  foreach(var order in new[]{new[]{"claims-a","claims-b"},new[]{"claims-b","claims-a"}}){
   var previews=Json(service.ChangeBatch(new(){Revision=service.GetSnapshot().Revision,RoleCode="admin",Players=order.Select(uid=>new RolePlayerRequest{PlayerUid=uid,ExpectedRoleCode="default"}).ToList()})).GetProperty("players").EnumerateArray().ToArray();
   for(int i=0;i<order.Length;i++){var expected=Json(service.PreviewPlayer(order[i],"admin")).GetProperty("effective");var actual=previews[i].GetProperty("effective");Check(actual.GetProperty("claimAllowance").GetInt32()==expected.GetProperty("claimAllowance").GetInt32()&&actual.GetProperty("claimAreas").GetInt32()==expected.GetProperty("claimAreas").GetInt32(),"Bulk claim preview depends on selection order");}
  }
  foreach(var invalid in new[]{Draft("bad role code"),Draft("invalidmode")}){if(invalid.Code=="invalidmode")invalid.DefaultGameMode="NoSuchMode";var request=new RoleEditorRequest{Revision=service.GetSnapshot().Revision,Action="create",Role=invalid};Check(!Json(service.PreviewDefinition(request)).GetProperty("success").GetBoolean()&&!service.Change(request).Success,"Preview/save validation disagree");}
  var createPreview=new RoleEditorRequest{Revision=service.GetSnapshot().Revision,Action="create",Role=Draft("previewonly")};Check(Json(service.PreviewDefinition(createPreview)).GetProperty("success").GetBoolean()&&!fixture.Config.RolesByCode.ContainsKey("previewonly"),"Creation preview mutated engine definitions");
  var lastAdminPreview=Draft("admin");Check(!Json(service.PreviewDefinition(new(){Revision=service.GetSnapshot().Revision,Role=lastAdminPreview})).GetProperty("success").GetBoolean(),"Preview accepted removal of the last admin privileges");
  Console.WriteLine("PASS UI1: independent bulk claim previews in both orders; invalid role codes/modes and last-admin preview rejected; valid creation preview does not save.");
  fixture.FailStore=true;Check(!service.Change(new(){Revision=service.GetSnapshot().Revision,Action="create",Role=Draft("failed")}).Success&&!fixture.Config.RolesByCode.ContainsKey("failed"),"Failed storage changed role definitions");fixture.FailStore=false;
  fixture.FailStoreNumber=fixture.Stores+2;var partial=service.Change(new(){Revision=service.GetSnapshot().Revision,Action="create",Role=Draft("failedcolor")});Check(!partial.Success&&!policy.HomeRoleLimits.ContainsKey("failedcolor")&&!list.Config.RoleNameColors.ContainsKey("failedcolor")&&!fixture.Config.RolesByCode.ContainsKey("failedcolor"),"Partial color persistence failure did not roll back shared state");
  Check(fixture.DirtyMarks>=3,"Engine configuration not marked for persistence");
  var custom=new SuiteCustomizationConfig{Theme="bogus",CardRadius=0,LogoUrl="javascript:alert(1)",BackgroundHex="bad"};custom.Clamp();Check(custom.CardRadius==0&&custom.Theme=="original"&&custom.LogoUrl==""&&custom.BackgroundHex=="#101820","Customization validation");
  custom.SurfaceTexture="bad";custom.TextureStrength=999;custom.Clamp();Check(custom.SurfaceTexture=="slate"&&custom.TextureStrength==25,"Texture bounds/type validation");custom.TextureStrength=-10;custom.Clamp();Check(custom.TextureStrength==0,"Negative texture strength");
  custom.LogoUrl="//evil.invalid/image";custom.Clamp();Check(custom.LogoUrl=="","Protocol relative logo accepted");custom.LogoUrl="https://example.invalid/logo.png";custom.Clamp();Check(custom.LogoUrl.StartsWith("https"),"Valid custom logo rejected");
  Check(!new BlockLogger.AntiCheat.AntiCheatConfig().OreShieldEnabled,"Fresh OreShield default on");
  using(var console=new WebConsoleService(api)){console.StartChatCapture();console.StartChatCapture();Check(fixture.ChatAdds==1,"Duplicate chat subscriptions");var player=(IServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(TestPlayer));var handler=typeof(WebConsoleService).GetMethod("OnPlayerChat",BindingFlags.NonPublic|BindingFlags.Instance)!;
   void Chat(string message,int channel,bool consumed=false)=>handler.Invoke(console,new object[]{player,channel,message,"",new BoolRef {value=consumed}});
   Chat("/home",GlobalConstants.GeneralChatGroup);Chat("private",999);Chat("blocked",GlobalConstants.GeneralChatGroup,true);Chat("Hello <img src=x>",GlobalConstants.GeneralChatGroup);
   Check(console.GetSnapshot(50).ChatLines.Count==0,"Chat included by default");Check(console.GetSnapshot(50,true).ChatLines.Count==1,"Command/private/consumed chat leaked");
   for(int i=0;i<600;i++)Chat("public "+i,GlobalConstants.GeneralChatGroup);Check(console.GetSnapshot(50,true).ChatLines.Count==500,"Chat ring unbounded");
  }Check(fixture.ChatRemoves==1,"Chat capture not unsubscribed");
  string db=Path.Combine(root,"storage.db");using(var conn=new SqliteConnection("Data Source="+db)){conn.Open();using var cmd=conn.CreateCommand();cmd.CommandText="CREATE TABLE block_events(id INTEGER PRIMARY KEY, payload BLOB);CREATE INDEX block_events_payload ON block_events(payload);CREATE TABLE small_notes(id INTEGER PRIMARY KEY,note TEXT);WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<600) INSERT INTO block_events SELECT n,zeroblob(2048) FROM seq;INSERT INTO small_notes VALUES(1,'hello');";cmd.ExecuteNonQuery();}
  var analyzer=mod.GetType("BlockLogger.WebDashboard.DatabaseStorageAnalyzer")!;
  var analyze=analyzer.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Single(m=>m.Name=="Analyze"&&m.GetParameters().Length==7);
  var exact=(DatabaseStorageDatabaseSnapshot)analyze.Invoke(null,new object[]{"blocklogger","Block Logger",db,0,0L,30,true})!;
  Check(exact.Detailed&&exact.DbStatAvailable&&exact.Tables.Count==2,"System inspector failed: "+exact.AnalysisMessage);Check(exact.Tables[0].Name=="block_events"&&exact.Tables[0].IndexBytes>0&&exact.TableDataBytes>1_000_000,"Exact attribution incomplete");
  var estimate=(DatabaseStorageDatabaseSnapshot)analyze.Invoke(null,new object[]{"blocklogger","Block Logger",db,0,0L,30,false})!;
  Check(estimate.PayloadEstimates&&!estimate.Detailed&&estimate.Tables.Count==2,"Portable estimates missing");var big=estimate.Tables[0];Check(big.Name=="block_events"&&big.RowCountKnown&&big.ApproxRows==600&&big.SampledRows==256&&big.EstimatedPayloadBytes>1_200_000&&big.IndexBytes==0&&big.SizeEstimated,"Fallback fabricated or missing statistics");
  var da=AssemblyLoadContext.Default.LoadFromAssemblyPath(discord);var dt=da.GetType("NewfiesServerEssentials.Discord.Discord.DiscordCommandService")!;var format=dt.GetMethod("FormatPlayerPage",BindingFlags.Static|BindingFlags.NonPublic)!;
  var page=(string)format.Invoke(null,new object[]{Enumerable.Range(1,200).Select(i=>"Player"+i).ToArray(),250,2})!;Check(page.Contains("Page 2/8")&&page.Contains("Player26")&&!page.Contains("Player1,"),"Discord pagination broken");
  var longPage=(string)format.Invoke(null,new object[]{Enumerable.Repeat(new string('*',120),50).ToArray(),100,1})!;Check(longPage.Length<=1900,"Discord reply exceeds budget");
  var bot=dt.GetConstructors().Single().Invoke(new object?[]{api,null,null,null,null});var queueMethod=dt.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name=="OnMainThread"&&m.IsGenericMethod).MakeGenericMethod(typeof(int));int executed=0;
  var queued=(Task<int>)queueMethod.Invoke(bot,new object[]{new System.Func<int>(()=>{executed++;return 1;})})!;
  try{queued.GetAwaiter().GetResult();throw new Exception("Queued Discord work did not time out");}catch(TimeoutException){}
  foreach(var task in fixture.Queued)task();Check(executed==0,"Expired Discord work executed later");
  Console.WriteLine("PASS roles: engine create/edit/delete, identity, stale revisions, offline assignments, last-admin/default/assigned-role safeguards, persistence failure. PASS chat: optional/private/command filtering, bounded ring, unsubscribe. PASS customization validation and fresh OreShield default. PASS database: exact system pages/indexes and portable labeled estimates. PASS Discord page and response bounds. No live server or Discord connection.");
 }finally{Directory.Delete(root,true);}
}
public class ApiProxy:DispatchProxy{
 public Fixture F=null!;
 protected override object? Invoke(MethodInfo? method,object?[]? args){string name=method!.Name;
  if(name=="EnqueueMainThreadTask"){F.Queued.Add((Action)args![0]!);return null;}
  if(name=="get_Config")return F.Config;if(name=="get_PlayerData")return F.Manager;
  if(name=="GetRole")return F.Config.RolesByCode.GetValueOrDefault((string)args![0]!);
  if(name=="get_PlayerName")return "Chat Alice";if(name=="get_PlayerUID")return "chat-alice";if(name=="get_Role")return F.Config.Roles[0];
  if(name=="get_ObjectCache")return F.Cache;if(name=="GetOrCreateDataPath")return F.Root;
  if(name=="StoreModConfig"){F.Stores++;if(F.FailStore||F.Stores==F.FailStoreNumber)throw new IOException("Fixture disk failure");return null;}
  if(name=="LoadModConfig"||name=="GetData"||name=="Get")return null;
  if(name=="MarkConfigDirty"){F.DirtyMarks++;return null;}if(name=="add_PlayerChat"){F.ChatAdds++;return null;}if(name=="remove_PlayerChat"){F.ChatRemoves++;return null;}
  return F.Make(method.ReturnType);
 }
}
public class Fixture{
 public readonly string Root;public int Stores,DirtyMarks,ChatAdds,ChatRemoves,FailStoreNumber;public bool FailStore;
 public readonly List<Action> Queued=new();
 public readonly Dictionary<string,object> Cache=new();public readonly ServerConfig Config;public readonly PlayerDataManager Manager;
 readonly Dictionary<Type,object> proxies=new();
 public Fixture(string root){Root=root;Config=(ServerConfig)RuntimeHelpers.GetUninitializedObject(typeof(ServerConfig));Config.DefaultRoleCode="default";Config.Roles=new(){new PlayerRole{Code="default",Name="Survivor",Privileges=new(){"chat"},DefaultGameMode=EnumGameMode.Survival},new PlayerRole{Code="admin",Name="Admin",Privileges=new(){"controlserver"},DefaultGameMode=EnumGameMode.Creative}};Config.RolesByCode=Config.Roles.ToDictionary(r=>r.Code);Manager=(PlayerDataManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayerDataManager));Manager.PlayerDataByUid=new();}
 public object? Make(Type type){if(type==typeof(void))return null;if(type==typeof(string))return "";if(type.IsArray)return Array.CreateInstance(type.GetElementType()!,0);if(type.IsValueType)return Activator.CreateInstance(type);if(type.IsGenericType&&!type.IsInterface&&type.GetConstructor(Type.EmptyTypes)!=null)return Activator.CreateInstance(type);if(!type.IsInterface)return null;if(proxies.TryGetValue(type,out var cached))return cached;var proxy=DispatchProxy.Create(type,typeof(ApiProxy));((ApiProxy)proxy).F=this;proxies[type]=proxy;return proxy;}
}

public class TestPlayer:ServerPlayer{public TestPlayer():base(null!,null!){} public override IPlayerRole Role{get=>new PlayerRole{Code="default",Name="Survivor"};set{}} public override string PlayerUID=>"chat-alice";}
