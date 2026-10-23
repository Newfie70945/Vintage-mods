using BlockLogger.WebDashboard;
int checks=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
string a=LogIssueEvidence.Origin("Error NullReferenceException\n at System.Reflection.Invoke()\n at Example.First.Run_Patch3(Int32 x)");
Check(a=="Example.First.Run_Patch","framework frame skipped; Harmony suffix stable");
Check(LogIssueEvidence.Origin("at Example.First.Run_Patch9()") == a,"patch counter stable");
Check(LogIssueEvidence.WithOrigin("error",a)!=LogIssueEvidence.WithOrigin("error","Example.Other.Run"),"different stack origins split identical errors");
Check(LogIssueEvidence.Origin("Error without stack")=="","no invented origin");
Check(LogIssueEvidence.IsNativeBuildLogger("VintagestoryLib.Server.Systems.ServerSystemBlockLogger.DidBreakBock"),"native logger identified");
Check(!LogIssueEvidence.IsNativeBuildLogger("BlockLogger.Logger.Write"),"Suite logger distinct");
var counts=new RecentLogIssueCounts();long now=1000000;
counts.Observe(now);counts.Observe(now);Check(counts.Count(now)==2,"recent counts");
Check(counts.Count(now-5000)==0,"future buckets excluded");
Check(counts.Count(now+300000)==0,"expired buckets excluded");
counts.Observe(now+300000);Check(counts.Count(now+300000)==1,"ring wraps without retaining old count");
for(int i=1;i<=10000;i++)counts.Observe(now+300000+i*5000L);
Check(counts.Count(now+300000+10000*5000L)==60,"bounded rolling retention");
Console.WriteLine($"PASS {checks} log evidence checks.");
