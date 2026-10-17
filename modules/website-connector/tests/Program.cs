using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Reflection.Emit;
using Newfies.WebsiteConnector;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static int FreePort() { var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return port; }
var runtimeAssembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UpdatedGameFixture"),AssemblyBuilderAccess.Run);
var runtimeModule=runtimeAssembly.DefineDynamicModule("game");
var versionType=runtimeModule.DefineType("GameVersion",TypeAttributes.Public);
versionType.DefineField("ShortGameVersion",typeof(string),FieldAttributes.Public|FieldAttributes.Static|FieldAttributes.Literal).SetConstant("1.22.9-rc.1");
Check(RuntimeGameVersion.Read(versionType.CreateType()!)=="1.22.9-rc.1","Version did not come from loaded runtime metadata");
var fallbackType=runtimeModule.DefineType("FallbackGameVersion",TypeAttributes.Public);
fallbackType.DefineField("OverallVersion",typeof(string),FieldAttributes.Public|FieldAttributes.Static|FieldAttributes.Literal).SetConstant("1.22.8");
Check(RuntimeGameVersion.Read(fallbackType.CreateType()!)=="1.22.8","Runtime version fallback failed");
Check(RuntimeGameVersion.Read(typeof(object))=="","Missing runtime version should remain unknown");
var cfg=new ConnectorConfig {Port=FreePort(),SuitePublicOrigin=$"http://127.0.0.1:{FreePort()}",Supporters=[new Supporter{Name="Named by owner",Tier="Patron",Since="2026"}]};
cfg.Normalize();Check(cfg.AccessToken.Length==64,"Token generation");
foreach(var origin in new[]{"https://example.com","http://127.0.0.1:9465/api/login","http://user:pass@localhost:9465","http://localhost:9465?redirect=1"})
{ try {new ConnectorConfig{SuitePublicOrigin=origin}.Normalize();throw new Exception("Accepted unsafe origin");} catch(ArgumentException){} }
Check(!PublicContract.Authorized("Bearer wrong",cfg.AccessToken),"Wrong secret accepted");
var hidden=PublicContract.Sanitize("/api/map",JsonNode.Parse("{\"showPlayers\":false,\"showWaypoints\":false,\"players\":[{\"name\":\"Hidden\",\"uid\":\"private\"}],\"waypoints\":[{\"name\":\"Hidden place\"}],\"dashboardUrl\":\"private\",\"activity\":[1]}")!.AsObject());
Check(hidden["players"]!.AsArray().Count==0&&hidden["waypoints"]!.AsArray().Count==0&&!hidden.ContainsKey("dashboardUrl")&&!hidden.ContainsKey("activity"),"Hidden map leaks");
var calls=0;var upstream=new TcpListener(IPAddress.Loopback,new Uri(cfg.SuitePublicOrigin).Port);upstream.Start();using var cancel=new CancellationTokenSource();
var fake=Task.Run(async()=>{
 try {while(!cancel.IsCancellationRequested){var client=await upstream.AcceptTcpClientAsync(cancel.Token);_=Task.Run(async()=>{using(client){var stream=client.GetStream();var data=new byte[8192];int n=await stream.ReadAsync(data);string line=Encoding.ASCII.GetString(data,0,n);var path=line.Split(' ')[1];Interlocked.Increment(ref calls);await Task.Delay(40);string body="{\"showPlayers\":true,\"activePlayerCount\":2,\"players\":[{\"name\":\"Public\",\"x\":4,\"uid\":\"secret-id\"}],\"dashboardUrl\":\"secret-admin\"}";var bytes=Encoding.UTF8.GetBytes(body);string type=path.EndsWith(".png")||path.Contains(".png?")?"image/png":"application/json";await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));await stream.WriteAsync(bytes);}});}}
 catch(OperationCanceledException){}
});
using var connector=new ConnectorServer(cfg,()=>Encoding.UTF8.GetBytes("{\"available\":true,\"online\":true,\"players\":7}"));connector.Start();
using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{cfg.Port}")};
Check((await http.GetAsync("/api/health")).StatusCode==HttpStatusCode.Unauthorized,"Unauthenticated read");
http.DefaultRequestHeaders.Authorization=new("Bearer",cfg.AccessToken);
using(var health=JsonDocument.Parse(await http.GetStringAsync("/api/health")))Check(health.RootElement.GetProperty("connectorVersion").GetString()=="1.1.1","Health version not bumped");
Check(connector.Status.Contains("1.1.1"),"Status command version not bumped");
foreach(var path in new[]{"/api/login","/api/config","/tiles/1/1.png?admin=1","/api/map?v=123","/styles/admin/tiles/1/1.png"})Check((await http.GetAsync(path)).StatusCode==HttpStatusCode.NotFound,"Unsafe path "+path);
Check((await http.PostAsync("/api/players",new StringContent("{}"))).StatusCode==HttpStatusCode.MethodNotAllowed,"Write accepted");
var results=await Task.WhenAll(Enumerable.Range(0,10).Select(_=>http.GetStringAsync("/api/players")));
Check(calls==1,"Concurrent snapshots fetched repeatedly");Check(results.All(s=>!s.Contains("secret")&&s.Contains("Public")),"Private field leaked");
Check((await http.GetStringAsync("/api/status")).Contains("7"),"Server status unavailable");
Check((await http.GetStringAsync("/api/community")).Contains("Named by owner"),"Supporters absent");
Check((await http.GetAsync("/styles/topo/lod/4/-2/3.png?v=100")).StatusCode==HttpStatusCode.OK,"Valid tile blocked");
async Task<string> Raw(string request){using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,cfg.Port);var s=c.GetStream();await s.WriteAsync(Encoding.ASCII.GetBytes(request));using var r=new StreamReader(s);return await r.ReadToEndAsync();}
Check((await Raw($"GET /api/health HTTP/1.1\r\nAuthorization: Bearer {cfg.AccessToken}\r\nAuthorization: Bearer {cfg.AccessToken}\r\n\r\n")).Contains("400 Bad Request"),"Duplicate auth accepted");
Check((await Raw($"GET /api/health HTTP/1.1\r\nAuthorization: Bearer {cfg.AccessToken}\r\nTransfer-Encoding: chunked\r\n\r\n")).Contains("400 Bad Request"),"Body framing accepted");
Check((await Raw("GET /api/health HTTP/1.1\r\nX-Large: "+new string('x',9000)+"\r\n\r\n")).Contains("431 Request Header Fields Too Large"),"Unbounded headers");
// Account linking exposes only secret-authenticated challenge/status routes over HTTP.
var challengeSecret=new string('a',64);
var create=await http.PostAsync("/api/link/challenge",new StringContent(JsonSerializer.Serialize(new { secret=challengeSecret }),Encoding.UTF8,"application/json"));
Check(create.StatusCode==HttpStatusCode.OK,"Private linking challenge unavailable");
using var challengeDoc=JsonDocument.Parse(await create.Content.ReadAsStringAsync());var linkingCode=challengeDoc.RootElement.GetProperty("code").GetString()!;
Check(linkingCode.Length==10,"Linking code length");
Check(connector.Links.Confirm(linkingCode,"FixtureUid","FixturePlayer").Contains("confirmed",StringComparison.OrdinalIgnoreCase),"In-game identity confirmation failed");
Check(connector.Links.Confirm(linkingCode,"OtherUid","OtherPlayer").Contains("already"),"Confirmed code reassigned");
var proof=await http.PostAsync("/api/link/status",new StringContent(JsonSerializer.Serialize(new { secret=challengeSecret }),Encoding.UTF8,"application/json"));
Check((await proof.Content.ReadAsStringAsync()).Contains("FixtureUid"),"Private confirmed proof missing");
Check((await http.GetAsync("/api/link/status")).StatusCode==HttpStatusCode.NotFound,"Proof accessible via public GET");
Check((await http.PostAsync("/api/link/confirm",new StringContent("{}"))).StatusCode==HttpStatusCode.MethodNotAllowed,"HTTP identity confirmation accepted");
Check((await http.PostAsync("/api/link/challenge",new StringContent("{bad json}",Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.BadRequest,"Malformed linking JSON accepted");
Check((await http.PostAsync("/api/link/challenge",new StringContent(JsonSerializer.Serialize(new {secret="bad"}),Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.BadRequest,"Short challenge secret accepted");
long fakeTime=1800000000000;var expiring=new AccountLinking(true,()=>fakeTime);var value=JsonSerializer.SerializeToElement(expiring.Create(new string('b',64)));var expiryCode=value.GetProperty("code").GetString()!;fakeTime+=600001;Check(expiring.Confirm(expiryCode,"ExpireUid","Player").Contains("expired"),"Expired challenge usable");
var attemptsRegistry=new AccountLinking(true,()=>fakeTime);for(int i=0;i<5;i++)attemptsRegistry.Confirm("AAAAAAAAAA","RateUid","Player");Check(attemptsRegistry.Confirm("AAAAAAAAAA","RateUid","Player").Contains("Too many"),"Unlimited code guesses");
var disabled=new AccountLinking(false);Check(disabled.Confirm("AAAAAAAAAA","Uid","Player").Contains("disabled"),"Disabled linking accepted");
if(args.Contains("--serve")){Console.WriteLine($"SMOKE_CONNECTOR_ORIGIN=http://127.0.0.1:{cfg.Port}");Console.WriteLine("SMOKE_TOKEN="+cfg.AccessToken);await Task.Delay(Timeout.Infinite);}
connector.Dispose();cancel.Cancel();upstream.Stop();await fake;
Console.WriteLine("PASS: token generation, local origin policy, hidden map privacy, auth, read-only routes, cache coalescing, supporters, tiles, duplicate headers, body framing, bounded headers, private linking, expiry, replay/guess protection and shutdown.");
