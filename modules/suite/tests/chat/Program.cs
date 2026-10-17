using Newfies.ChatProtocol;
using BlockLogger.Services;
using BlockLogger.Integrations;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;

static class Program {
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 static void Main(){
 MarkdownTests();
 LayoutTests();
 string[] commands={"/home","  /home base","<strong>Alice:</strong> /home","<strong>Alice</strong>: /home","<font color=\"#ff0000\"><strong>Alice:</strong></font> /home", "<strong>[Admin]</strong> <font color=\"red\"><strong>Nick</strong></font>: /home"};
 foreach(var m in commands)Check(ChatMessageBoundary.Parse(m,"Alice","Nick","Admin").IsCommand,"Command boundary: "+m);
 foreach(var m in new[]{"Alice: mention /home","Bob: /home","Alice: &#47;home","Alice: <b>/home</b>","Alice: Alice: /home","<malformed Alice: /home"})Check(!ChatMessageBoundary.Parse(m,"Alice").IsCommand,"Authored text must stay chat: "+m);
 var api=new Api();var p=new Player();api.World.AllOnlinePlayers=new IPlayer[]{p};
 using(var service=new ChatDisplayService(api,resolveDisplayName:(_,_)=>"Nick")){
 service.Start();service.Config.ShowRoleBeforeName=true;
 foreach(var input in commands){string m=input,d="routing-data";var b=new BoolRef();int before=ChatGuardControlBridge.Calls;api.Event.Chat(p,0,ref m,ref d,b);Check(m==input&&d=="routing-data"&&!b.value&&ChatGuardControlBridge.Calls==before,"Slash command altered or moderated: "+input);}
 string line="<strong>Alice</strong>: hello",data="routing-data";var consumed=new BoolRef();api.Event.Chat(p,0,ref line,ref data,consumed);Check(line.Contains("Nick")&&line.EndsWith("hello")&&data=="routing-data"&&!consumed.value&&ChatGuardControlBridge.Body=="hello","Normal chat styling/routing");
 line="<strong>Alice:</strong> nearby";string original=line;api.Event.Chat(p,5,ref line,ref data,consumed);Check(line==original&&!consumed.value,"Other chat channel changed");
 service.Config.ShowRoleBeforeName=false;service.Config.BoldPlayerName=false;
 line="Alice: **hello**";api.Event.Chat(p,0,ref line,ref data,consumed);
 Check(line=="Nick: <strong>hello</strong>"&&ChatGuardControlBridge.Body=="hello"&&!consumed.value&&data=="routing-data","Markdown-only mode / visible moderation / delivery");
 service.Config.MarkdownAllowedRoleCodes=new(){"MODERATOR"};
 line="Alice: **hello**";api.Event.Chat(p,0,ref line,ref data,consumed);
 Check(!line.Contains("<strong>hello")&&ChatGuardControlBridge.Body=="**hello**","Role restriction");
 service.Config.MarkdownAllowedRoleCodes=new(){"ADMIN"};service.Config.MarkdownAllowColors=true;service.Config.MarkdownAllowLinks=true;
 var saved=service.SaveSettings(new ChatDisplaySettingsRequest {BoldPlayerName=true,FormatGeneralChat=true});
 Check(saved.Success&&saved.Snapshot.MarkdownAllowColors&&saved.Snapshot.MarkdownAllowLinks&&saved.Snapshot.MarkdownAllowedRoleCodes.SequenceEqual(new[]{"ADMIN"}),"Older GUI save erased Markdown settings");
 saved=service.SaveSettings(new ChatDisplaySettingsRequest {MarkdownEnabled=false,MarkdownAllowColors=false,MarkdownAllowLinks=false,MarkdownAllowedRoleCodes=new(){" admin ","ADMIN","", "mod"}});
 Check(!saved.Snapshot.MarkdownEnabled&&saved.Snapshot.MarkdownAllowedRoleCodes.SequenceEqual(new[]{"admin","mod"}),"New settings roundtrip/clamp");
 ChatGuardControlBridge.Block=true;line="Alice: spam";api.Event.Chat(p,0,ref line,ref data,consumed);Check(consumed.value,"Intentional moderation no longer works");ChatGuardControlBridge.Block=false;
 }
 var brokenApi=new Api();
 using(var broken=new ChatDisplayService(brokenApi,resolveConfiguredNameColor:_=>throw new InvalidOperationException("test resolver"))){broken.Start();string m="Alice: hello",d="payload";var b=new BoolRef();brokenApi.Event.Chat(p,0,ref m,ref d,b);Check(m=="Alice: hello"&&d=="payload"&&!b.value&&brokenApi.Logger.Warnings==1,"Formatting failure interrupted chat");}
 GuideTests();
 Console.WriteLine("PASS: production chat boundary and ChatDisplay handler command/moderation/routing/failure cases (API doubles).");
 }
 static void LayoutTests(){
 Check(ChatLayout.Validate(ChatLayout.DefaultFormat)==null,"Default format valid");
 foreach(var bad in new[]{"{message}","{name}","{name} {message} {message}","{name} {message} {unknown}","{name} {message} }","/x {name} {message}","{name}\n{message}",new string('x',513)})Check(ChatLayout.Validate(bad)!=null,"Reject invalid format: "+bad);
 var api=new Api();var player=new Player();api.World.AllOnlinePlayers=new IPlayer[]{player};
 using var service=new ChatDisplayService(api);service.Start();
 service.Config.ShowRoleBeforeName=true;service.Config.ShowTimestampBeforeMessage=true;service.Config.TimestampFormat="'21:35'";
 service.Config.ChatFormat="[Global] {role} {name} {timestamp}: {message} [end]";
 string line="Alice: **hello**",data="original routing";var consumed=new BoolRef();api.Event.Chat(player,0,ref line,ref data,consumed);
 Check(line.StartsWith("[Global] <font")&&line.Contains("</font> <strong>Alice</strong> [21:35]: <strong>hello</strong> [end]"),"Custom rank/name/time order: "+line);
 Check(data=="original routing"&&!consumed.value,"Custom layout keeps delivery path");
 var tree=new TreeAttribute();tree.SetString("playerUid",player.PlayerUID);tree.SetInt("channelId",0);tree.SetString("body",line);api.Event.PushEvent(ChatDisplayService.PresentationQueryEvent,tree);
 Check(tree.GetBool("available")&&tree.GetString("visibleText")=="hello","Relay receives canonical message without layout");
 tree=new TreeAttribute();tree.SetString("playerUid",player.PlayerUID);tree.SetInt("channelId",0);tree.SetString("body",line);api.Event.PushEvent(ChatDisplayService.ModerationTextQueryEvent,tree);
 Check(tree.GetString("moderationText")=="hello","Late moderation sees message once");
 service.Config.MarkdownEnabled=false;line="Alice: literal **body**";api.Event.Chat(player,0,ref line,ref data,consumed);
 tree=new TreeAttribute();tree.SetString("playerUid",player.PlayerUID);tree.SetInt("channelId",0);tree.SetString("body",line);api.Event.PushEvent(ChatDisplayService.PresentationQueryEvent,tree);
 Check(tree.GetString("visibleText")=="literal **body**","Canonical aliases also work with Markdown off");
 string before=service.Config.ChatFormat;var saved=service.SaveSettings(new ChatDisplaySettingsRequest {BoldPlayerName=true});Check(saved.Success&&service.Config.ChatFormat==before,"Older GUI preserves layout");
 saved=service.SaveSettings(new ChatDisplaySettingsRequest {ChatFormat="{message}"});Check(!saved.Success&&service.Config.ChatFormat==before,"Invalid layout never replaces live config");
 Check(ChatLayout.Render(ChatLayout.DefaultFormat,"","Alice","","  spaced  ",x=>x)=="Alice:   spaced  ","Optional separators hidden; message whitespace preserved");
 Check(ChatLayout.Render("<fake>{name}: {message}","","<strong>Alice</strong>","","{role}",x=>x.Replace("<","&lt;").Replace(">","&gt;"))=="&lt;fake&gt;<strong>Alice</strong>: {role}","Literal escaped and message not reparsed");
 Console.WriteLine("PASS: chat layout validation, rank/name/time/prefix/suffix, escaping, older-client preservation, moderation/relay aliases with Markdown on/off, no delivery changes.");
 }
 static void MarkdownTests(){
 void Format(string input,string expected,bool colors=false,bool links=false){Check(ChatMarkdown.Format(input,colors,links).Vtml==expected,"Markdown: "+input+" => "+ChatMarkdown.Format(input,colors,links).Vtml);}
 Format("**bold** _italic_ ***both*** __also__","<strong>bold</strong> <i>italic</i> <strong><i>both</i></strong> <strong>also</strong>");
 Format("**bold with *italic* inside**","<strong>bold with <i>italic</i> inside</strong>");
 Format("**bold with *italic***","<strong>bold with <i>italic</i></strong>");
 Format("*italic with **bold***","<i>italic with <strong>bold</strong></i>");
 Format("*a **b** c*","<i>a <strong>b</strong> c</i>");
 Format("**a *b* c**","<strong>a <i>b</i> c</strong>");
 Format("__bold _italic___","<strong>bold <i>italic</i></strong>");
 Format("**one** **two**","<strong>one</strong> <strong>two</strong>");
 Format("**outer *unmatched**","<strong>outer *unmatched</strong>");
 Format("*outer **unmatched*","<i>outer **unmatched</i>");
 Format("*bad close * then good*","<i>bad close * then good</i>");
 Format("*****literal*****","*****literal*****");
 Format("**hello**","<strong>hello</strong>");
 Format("player_name **unclosed","player_name **unclosed");
 Format("\\*literal\\* and \\**literal\\**","*literal* and **literal**");
 Format("`**literal** <font>`","<code>**literal** &lt;font&gt;</code>");
 Format("**code `**literal**` inside**","<strong>code <code>**literal**</code> inside</strong>");
 Format("<a href=\"https://bad\">raw</a>","&lt;a href=\"https://bad\"&gt;raw&lt;/a&gt;");
 Format("[color=#A1b2C3]**blue**[/color]","<font color=\"#A1b2C3\"><strong>blue</strong></font>",true);
 Format("[color=#FFFFFF]a [color=#80C8FF]b[/color] c[/color]","<font color=\"#FFFFFF\">a <font color=\"#80C8FF\">b</font> c</font>",true);
 Format("[color=#FFFFFF]`[/color]` still white[/color]","<font color=\"#FFFFFF\"><code>[/color]</code> still white</font>",true);
 Format("[color=#FFFFFF]a \\[color=#80C8FF]literal[/color]","<font color=\"#FFFFFF\">a [color=#80C8FF]literal</font>",true);
 Format("[color=#FFFFFF]a [color=#80C8FF]b[/color]","[color=#FFFFFF]a <font color=\"#80C8FF\">b</font>",true);
 Format("**[color=#80C8FF]*inside*[/color]**","<strong><font color=\"#80C8FF\"><i>inside</i></font></strong>",true);
 Format("[color=#GGGGGG]x[/color]","[color=#GGGGGG]x[/color]",true);
 Format("[color=#FFFFFF]x[/color]","[color=#FFFFFF]x[/color]");
 Format("[Site](https://example.com/a?x=1&y=2)","<a href=\"https://example.com/a?x=1&y=2\">Site</a>",links:true);
 Format("[Wiki](https://example.com/wiki/Thing_(game))","<a href=\"https://example.com/wiki/Thing_(game)\">Wiki</a>",links:true);
 Format("[\\[Site\\]](https://example.com)","<a href=\"https://example.com\">[Site]</a>",links:true);
 Format("*see [item](https://example.com/a*b)*","<i>see <a href=\"https://example.com/a*b\">item</a></i>",links:true);
 Format("`path\\`","<code>path\\</code>");
 foreach(var url in new[]{"javascript:alert", "file:///tmp/test", "https://owner:secret@example.com/", "https://example.com/\"x", "https://example.com/<tag>"})
 Format("[x]("+url+")",("[x]("+url+")").Replace("<","&lt;").Replace(">","&gt;"),links:true);
 Format("[Site](https://example.com)","[Site](https://example.com)");
 Check(ChatMarkdown.Format("bad**word** [link](https://example.com)",links:true).ModerationText=="badword link (https://example.com)","Visible moderation and link target");
 Check(ChatMarkdown.Format("[color=#FFFFFF]for[color=#80C8FF]bidden[/color]word[/color]",colors:true).ModerationText=="forbiddenword","Nested color visible moderation");
 Format("[color=#FFFFFF][\\[/color\\]](https://example.com) still white[/color]","<font color=\"#FFFFFF\"><a href=\"https://example.com\">[/color]</a> still white</font>",true,true);
 Check(ChatMarkdown.Format(new string('*',5000)).Vtml==new string('*',5000),"Oversized input fallback");
 var watch=System.Diagnostics.Stopwatch.StartNew();
 for(int i=0;i<100;i++)Check(ChatMarkdown.Format(string.Concat(Enumerable.Repeat("[x]( * __ `",350)),true,true).Vtml.Length<24000,"Output bound");
 Check(watch.Elapsed.TotalSeconds<5,"Pathological input performance");
 // Deterministic hostile-input regression: all generated tag trees must stay balanced.
 var random=new Random(240);string[] pieces={"*","**","***","_","__","___","`","\\","<script>","[color=#FFFFFF]","[/color]","[x](https://example.com/a_(b))","[", "]", "a", " "};
 for(int sample=0;sample<500;sample++){
 string input=string.Concat(Enumerable.Range(0,100).Select(_=>pieces[random.Next(pieces.Length)]));
 var result=ChatMarkdown.Format(input,true,true);var stack=new Stack<string>();
 foreach(System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(result.Vtml,@"<(/?)(strong|i|code|font|a)(?: [^>]*)?>")){
 string name=tag.Groups[2].Value;
 if(tag.Groups[1].Value=="/")Check(stack.Count>0&&stack.Pop()==name,"Unbalanced generated tags");else stack.Push(name);
 }
 Check(stack.Count==0&&!result.Vtml.Contains("<script>"),"Malformed input supplied raw markup");
 }
 Console.WriteLine("PASS: Markdown syntax, nested styles, escaping, URL allowlist, visible moderation and bounded input.");
 }
 static void GuideTests(){
 var api=new Api();var player=new Player();var service=new ChatDisplayService(api);service.Start();service.Start();
 var command=api.ChatCommands.Get("chatformat")!;
 Check(api.ChatCommands.Commands.Count==1&&command.PlayerRequired&&command.Privilege=="chat","Guide registration/access");
 var args=new TextCommandCallingArgs{Caller=new(){Player=player}};int before=ChatGuardControlBridge.Calls;
 string guide=command.Handler!(args).StatusMessage;
 Check(guide.Contains("enabled for your role")&&guide.Contains("Colors (off for you)")&&guide.Contains("**text**")&&guide.Contains("Other groups: off"),"Default guide");
 service.Config.MarkdownAllowColors=true;service.Config.MarkdownAllowLinks=true;
 guide=command.Handler(args).StatusMessage;Check(guide.Contains("Colors (enabled)")&&guide.Contains("Links (enabled)"),"Live guide settings");
 service.Config.MarkdownAllowedRoleCodes=new(){"moderator"};Check(command.Handler(args).StatusMessage.Contains("not enabled for your role"),"Guide role restriction");
 service.Config.MarkdownEnabled=false;Check(command.Handler(args).StatusMessage.Contains("disabled on this server"),"Guide disabled state");
 Check(ChatGuardControlBridge.Calls==before&&command.Handler(new()).Failed,"Private guide avoids moderation; player required");
 using(var collision=new ChatDisplayService(api)){collision.Start();Check(ReferenceEquals(command,api.ChatCommands.Get("chatformat"))&&api.Logger.Warnings==1,"Existing command overwritten");}
 service.Dispose();Check(command.Handler(args).Failed,"Disposed guide");
 Console.WriteLine("PASS: private /chatformat guide, registration collision, live settings/role scope and disposed command.");
 }
 sealed class Role:IPlayerRole {public string Name=>"Admin";public string Code=>"admin";public System.Drawing.Color Color=>System.Drawing.Color.White;}
 sealed class Player:IServerPlayer {public string PlayerUID=>"uid";public string PlayerName=>"Alice";public IPlayerRole Role {get;}=new Role();}
 sealed class Api:ICoreServerAPI {public FakeEvents Event {get;}=new();public FakeWorld World {get;}=new();public FakeLogger Logger {get;}=new();public FakeCommands ChatCommands {get;}=new();}
}
