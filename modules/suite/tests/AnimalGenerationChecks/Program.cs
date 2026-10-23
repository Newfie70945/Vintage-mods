using System.Reflection;
using System.Runtime.Loader;
using BlockLogger.AnimalLimiter;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
var game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetFullPath("artifacts/build/suite")}){var p=Path.Combine(folder,name.Name+".dll");if(File.Exists(p))return AssemblyLoadContext.Default.LoadFromAssemblyPath(p);}return null;};
Checks.Run();
static class Checks
{
 public static void Run()
 {
  int count=0;void Check(bool yes,string why){if(!yes)throw new Exception(why);count++;}
  var system=new AnimalLimiterSystem();var type=typeof(AnimalLimiterSystem);const BindingFlags f=BindingFlags.Instance|BindingFlags.NonPublic;
  var config=new AnimalLimiterConfig { ProtectPets=true,ProtectNamedEntities=true,ProtectFarmAnimals=true,GenerationFilter="ProtectBred",Rules=new(){{"test:raccoon-*",20}} };
  type.GetField("config",f)!.SetValue(system,config);
  void Normalize(){type.GetMethod("NormalizeConfig",f)!.Invoke(system,null);((System.Collections.IDictionary)type.GetField("resolvedRuleCache",f)!.GetValue(system)!).Clear();}
  bool Protected(EntityAgent e,out string why){object?[] args={e,null};var result=(bool)type.GetMethod("IsProtected",f)!.Invoke(system,args)!;why=(string)args[1]!;return result;}
  EntityAgent Animal(string code="test:raccoon-adult-male",int generation=3){var e=new EntityAgent{Code=new AssetLocation(code)};e.WatchedAttributes.SetInt("generation",generation);return e;}
  Normalize();Check(Protected(Animal(),out _),"legacy rules still protect bred animals");
  config.RuleGenerationPolicies["test:raccoon-*"]=new(){Mode="Off"};Normalize();
  Check(!Protected(Animal(),out _),"Off bypasses both global farm and generation filters");
  var pet=Animal();pet.WatchedAttributes.SetString("owneruid","owner");Check(Protected(pet,out var why)&&why=="pet/owned","owned animals stay protected");
  var named=Animal();named.WatchedAttributes.SetString("customName","Pet");Check(Protected(named,out why)&&why=="named","named animals stay protected");
  config.ExcludeRules.Add("test:raccoon-*");Check(Protected(Animal(),out why)&&why=="excluded","exclusions stay protected");config.ExcludeRules.Remove("test:raccoon-*");
  Check(Protected(Animal("other:raccoon-adult-male"),out _),"unmatched species keep global protection");
  config.RuleGenerationPolicies["test:raccoon-*"]=new(){Mode="ProtectFromGeneration",MinimumProtectedGeneration=4};Normalize();
  foreach(int gen in new[]{0,1,3})Check(!Protected(Animal(generation:gen),out _),"generation below threshold eligible");
  foreach(int gen in new[]{4,5,100})Check(Protected(Animal(generation:gen),out why)&&why=="rule generation threshold protected","threshold inclusive protection");
  var longGen=Animal();longGen.WatchedAttributes.SetLong("generation",4);Check(Protected(longGen,out _),"long generation");
  var doubleGen=Animal();doubleGen.WatchedAttributes.SetDouble("generation",4);Check(Protected(doubleGen,out _),"double generation");
  var boolGen=Animal();boolGen.WatchedAttributes.SetBool("generation",true);Check(!Protected(boolGen,out _),"boolean generation cannot satisfy threshold four");
  config.RuleGenerationPolicies["test:raccoon-*"].MinimumProtectedGeneration=1;Check(Protected(boolGen,out _),"boolean generation supports threshold one");
  var negative=Animal(generation:-2);Check(!Protected(negative,out _),"negative numeric generation does not satisfy positive threshold");
  var nan=Animal();nan.WatchedAttributes.SetDouble("generation",double.NaN);Check(!Protected(nan,out _),"nonfinite generation does not satisfy threshold");
  var stringGen=Animal();stringGen.WatchedAttributes.SetString("generation","true");Check(Protected(stringGen,out _),"legacy string flag supports threshold one");
  config.RuleGenerationPolicies["test:raccoon-*"].Mode="Off";config.Rules["test:raccoon-adult-female"]=10;Normalize();Check(Protected(Animal("test:raccoon-adult-female"),out _),"specific rule inherits global instead of broader override");
  config.RuleGenerationPolicies["test:raccoon-adult-female"]=new(){Mode="Off"};Normalize();Check(!Protected(Animal("test:raccoon-adult-female"),out _),"specific override wins");
  var saved=JsonConvert.DeserializeObject<AnimalLimiterConfig>(JsonConvert.SerializeObject(config))!;Check(saved.RuleGenerationPolicies["test:raccoon-adult-female"].Mode=="Off","policy persists JSON roundtrip");
  config.RuleGenerationPolicies["test:raccoon-*"]=new(){Mode="bogus"};Normalize();Check(!config.RuleGenerationPolicies.ContainsKey("test:raccoon-*"),"invalid disk mode safely inherits");
  Check(Protected(Animal(),out _),"invalid disk mode retains global protections");
  Check(system.SetRuleFromGui("test:raccoon-*",1,1,"bogus",1).StartsWith("Invalid"),"API rejects invalid policy before saving");
  Check(system.SetRuleFromGui("test:raccoon-*",1,1,"ProtectFromGeneration",0).StartsWith("Invalid"),"API rejects invalid threshold");
  Check(config.Rules["test:raccoon-*"]==20,"invalid requests leave population cap unchanged");
  Console.WriteLine($"PASS {count} real Animal Limiter generation/protection/precedence/persistence checks; no entities removed.");
 }
}
