using BlockLogger.Services;
using Newtonsoft.Json;
using System.Diagnostics;
var directory=Path.Combine(Path.GetTempPath(),"nss-state-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
var path=Path.Combine(directory,"state.json");
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
try
{
 var state=new {SchemaVersion=3,Label="Unicode ☃ < &",Rows=Enumerable.Range(0,5000).Select(i=>new {Id=i,Message=new string('x',200),Values=new[]{1,2,3}}).ToArray()};
 AtomicJsonStateStore.Store(path,state);
 string original=File.ReadAllText(path);
 Check(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.Parse(original),Newtonsoft.Json.Linq.JToken.Parse(JsonConvert.SerializeObject(state))),"Streamed output changed stored JSON data");
 try{AtomicJsonStateStore.Store(path,new Broken());throw new Exception("Expected getter failure");}catch(JsonSerializationException){}
 Check(File.ReadAllText(path)==original,"Failed serialization destroyed previous state");
 Check(Directory.GetFiles(directory).Length==1,"Failed serialization leaked temporary file");
 var blocked=Path.Combine(directory,"blocked");Directory.CreateDirectory(blocked);
 try{AtomicJsonStateStore.Store(blocked,state);throw new Exception("Expected failed replacement");}catch(IOException){}
 Check(Directory.GetFiles(directory).Length==1,"Failed move leaked temporary file");
 AtomicJsonStateStore.Store(path,new {SchemaVersion=4,Rows=Array.Empty<int>()});
 Check(((int)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path))["SchemaVersion"]!)==4,"Successful replacement failed");
 void Stock()=>File.WriteAllText(path,JsonConvert.SerializeObject(state,Formatting.Indented));
 void Streamed()=>AtomicJsonStateStore.Store(path,state);
 object Measure(Action action){action();long before=GC.GetAllocatedBytesForCurrentThread(),stamp=Stopwatch.GetTimestamp();for(int i=0;i<5;i++)action();return new{iterations=5,bytesPerWrite=(GC.GetAllocatedBytesForCurrentThread()-before)/5d,msPerWrite=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds/5};}
 var measurements=new{stock=Measure(Stock),streamed=Measure(Streamed),scope="5000-row synthetic JSON state, allocation/write component only; not production TPS or heap reduction."};
 string report=System.Text.Json.JsonSerializer.Serialize(measurements);Console.WriteLine(report);
 if(Environment.GetEnvironmentVariable("NSS_STATE_MEASUREMENTS") is string output)File.WriteAllText(output,report+"\n");
 Console.WriteLine("PASS streamed state roundtrip, failed serialization/move preservation, cleanup and successful replacement.");
}
finally{Directory.Delete(directory,true);}
sealed class Broken{public int Value=>throw new InvalidOperationException("fixture failure");}
