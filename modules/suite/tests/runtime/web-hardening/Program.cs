using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string assembly = Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY");
var roots = new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(assembly)! };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (var root in roots) { var path = Path.Combine(root, name.Name + ".dll"); if (File.Exists(path)) return context.LoadFromAssemblyPath(path); }
    return null;
};
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(assembly);
string version = (string)mod.GetType("BlockLogger.SuiteRelease",true)!.GetField("Version")!.GetRawConstantValue()!;
if (mod.GetName().Version?.ToString(3) != version) throw new Exception("Assembly version mismatch");
var serviceType = mod.GetType("BlockLogger.WebDashboard.WebDashboardService", true)!;
object service = RuntimeHelpers.GetUninitializedObject(serviceType);
var read = serviceType.GetMethod("ReadRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
async Task<object?> Parse(string wire,int fragmentSize=0) {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try {
        using var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync(); await connect;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = (Task)read.Invoke(service, new object[] { server.GetStream(), timeout.Token })!;
        byte[] bytes=Encoding.ASCII.GetBytes(wire);
        if(fragmentSize==0)await client.GetStream().WriteAsync(bytes,timeout.Token);
        else for(int offset=0;offset<bytes.Length;offset+=fragmentSize)
        {await client.GetStream().WriteAsync(bytes.AsMemory(offset,Math.Min(fragmentSize,bytes.Length-offset)),timeout.Token);await Task.Delay(1,timeout.Token);}
        client.Client.Shutdown(SocketShutdown.Send);
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    } finally { listener.Stop(); }
}
var request = await Parse("POST /api/login HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2\r\n\r\n{}");
if ((string)request!.GetType().GetProperty("Body")!.GetValue(request)! != "{}") throw new Exception("Valid fixed-length request rejected");
await Parse("GET /api/session HTTP/1.1\r\nHost: localhost\r\n\r\n");
var fragmented=await Parse("GET /api/session?name=two+words&symbol=%2B HTTP/1.1\r\nHost: localhost\r\n\r\n",1);
var query=(System.Collections.IDictionary)fragmented!.GetType().GetProperty("Query")!.GetValue(fragmented)!;
if((string)query["name"]! != "two words" || (string)query["symbol"]! != "+")throw new Exception("Fragmented header/query decoding changed");
try{await Parse("POST /api/login HTTP/1.1\r\nContent-Length: 6\r\n\r\n{}");throw new Exception("Truncated request body accepted");}catch(EndOfStreamException){ }

string[] invalid = {
    "Content-Length: 2\r\ncontent-length: 3\r\n",
    "Transfer-Encoding: chunked\r\n",
    "Transfer-Encoding: chunked\r\nContent-Length: 2\r\n",
    "Content-Length: +2\r\n",
    "Content-Length: -2\r\n",
    "Content-Length: 131073\r\n",
    "Content-Length : 2\r\n",
    " Content-Length: 2\r\n",
    "BrokenHeader\r\n",
    "Cookie: a=1\r\nCookie: b=2\r\n",
    "X-Padding: " + new string('a', 32768) + "\r\n"
};
foreach (var header in invalid) {
    try { await Parse("POST /api/login HTTP/1.1\r\n" + header + "\r\n{}"); throw new Exception("Malformed headers accepted: " + header[..Math.Min(header.Length, 60)]); }
    catch (InvalidDataException) { }
}
var auth = serviceType.GetMethod("TryAuthenticateWebUser", BindingFlags.Instance | BindingFlags.NonPublic)!;
foreach (var credentials in new[] { (new string('a',257),"ok"), ("admin",new string('a',257)), (new string('a',65),"ok"), (" ","ok") }) {
    object?[] credentialsArgs = { credentials.Item1, credentials.Item2, "fixture", null, false };
    if ((bool)auth.Invoke(service,credentialsArgs)!) throw new Exception("Unbounded login accepted");
    // The uninitialized service has no config/accounts: a safe rejection proves
    // these inputs never reached password hashing or account persistence.
}
var configType = mod.GetType("BlockLogger.WebDashboard.WebDashboardConfig", true)!;
object config = Activator.CreateInstance(configType)!;
serviceType.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, config);
string passwordAtLimit = new string('p', 256);
serviceType.GetMethod("SetPasswordHash", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[] { passwordAtLimit });
object?[] validCredentials = { " admin ", passwordAtLimit, "fixture", null, false };
if (!(bool)auth.Invoke(service, validCredentials)!) throw new Exception("Valid owner credentials at the password limit rejected");
string build = (string)serviceType.GetField("BuildId")!.GetRawConstantValue()!;
string html = (string)mod.GetType("BlockLogger.WebDashboard.WebDashboardPage",true)!.GetField("Html")!.GetValue(null)!;
if (!html.Contains("const BUILD_ID = '"+build+"'") || !html.Contains("<div class=\"metricValue\">"+version+"</div>") || html.Contains("1.2.37") || html.Contains("__NSS_")) throw new Exception("WebPanel release identity mismatch");
Console.WriteLine("PASS: valid fixed-length/byte-fragmented requests and truncated body; 11 malformed/framing/header-limit cases; 4 bounded-login cases and valid owner login at the password limit; WebPanel/backend version and build consistency. Loopback sockets only; no game world.");
