using BlockLogger.AnimalLimiter;
var rng=new Random(91);int cases=0;
foreach(int n in new[]{0,1,7,32})
{
 var players=Enumerable.Range(0,n).Select(_=>(X:rng.NextDouble()*10000,Y:rng.NextDouble()*400,Z:rng.NextDouble()*10000)).ToList();
 for(int i=0;i<1000;i++)
 {
  double x=rng.NextDouble()*10000,y=rng.NextDouble()*400,z=rng.NextDouble()*10000;
  double expected=double.MaxValue;
  foreach(var p in players){double dx=p.X-x,dy=p.Y-y,dz=p.Z-z,d=dx*dx+dy*dy+dz*dz;if(d<expected)expected=d;}
  if(PopulationDistance.NearestSquared(x,y,z,players)!=expected)throw new Exception("distance changed");
  cases++;
 }
}
// Top-N before projection must preserve ordering, ties, sizes and values.
foreach(int n in new[]{0,1,100,200,1000})
{
 var data=Enumerable.Range(0,n).ToDictionary(i=>i,i=>rng.Next(20));
 foreach(int cap in new[]{100,120,200})
 {
  var original=data.Select(p=>new{Id=p.Key,Ms=p.Value}).OrderByDescending(p=>p.Ms).Take(cap).ToArray();
  var reduced=data.OrderByDescending(p=>p.Value).Take(cap).Select(p=>new{Id=p.Key,Ms=p.Value}).ToArray();
  if(!original.SequenceEqual(reduced))throw new Exception("ranking changed");cases++;
 }
}
Console.WriteLine($"PASS {cases} distance and stable top-N equivalence cases; no player workload claim.");
