using NewfiesServerEssentials.Discord.Relay;
int checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
var s=new StormTransitions();
Check(s.Observe(new(false,99,"Light")).Count==0,"startup baseline");
foreach(var step in new[]{(false,1,"approaching"),(false,0,"imminent"),(true,0,"started"),(true,-1,"waning")})
{
    var state=new StormState(step.Item1,step.Item2,"Light");var e=s.Observe(state);
    Check(e.Count==1&&e[0]==(step.Item3,"Light"),step.Item3);
    Check(s.Observe(state).Count==0,"no duplicate "+step.Item3);
}
var end=s.Observe(new(false,99,"Heavy"));
Check(end.Count==1&&end[0]==("ended","Light"),"end uses old strength");
s.Reset();Check(s.Observe(new(true,0,"Heavy")).Count==0,"reload active baseline");
// Disabled routes still call Observe: enabling must not replay the current phase.
s.Observe(new(true,-1,"Heavy"));Check(s.Observe(new(true,-1,"Heavy")).Count==0,"disabled phase not replayed");
Check(GameEventText.Format("{player}: {message}",new Dictionary<string,string>{{"player","{message}"},{"message","hello"}})=="{message}: hello","single-pass interpolation");
Check(GameEventText.Format("{message}",new Dictionary<string,string>{{"message","<strong>A</strong> &amp; B\n@everyone *_`"}})=="A & B @ everyone \\*\\_\\`","markup/markdown/mention boundary");
Check(GameEventText.Format("custom {player}",new Dictionary<string,string>{{"player","Name"}})=="custom Name","custom template retained");
var longText=GameEventText.Format("{message}",new Dictionary<string,string>{{"message",string.Concat(Enumerable.Repeat("😀",2000))}});
Check(longText.Length<=1800&&!char.IsHighSurrogate(longText[^2]),"bounded Unicode output");
foreach(var c in new[]{("Hunger","Internal","","starved"),("Electricity","Block","","lightning"),("Fire","Internal","","burned"),("Suffocation","Internal","","suffocated"),("BluntAttack","Entity","wolf","wolf"),("Gravity","Fall","","fell"),("Injury","Explosion","","explosion"),("Other","Unknown","","Other")})
Check(GameEventText.Fallback("Name",c.Item1,c.Item2,c.Item3).Contains(c.Item4),"fallback "+c.Item1);
Console.WriteLine($"PASS: {checks} Discord event checks");
