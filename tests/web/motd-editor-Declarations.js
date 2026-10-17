let communityMotdData=null;
let communityMotdConfig=null;
let communityMotdContentTab='main';
let communityMotdBaseline=null,communityMotdBaselineForm='',communityMotdDirty=false,communityMotdRevision=0,communityMotdBusy=false,communityMotdLoading=false,motdPreviewTimer=null;
const COMMUNITY_MOTD_CONTENT_KEYS={main:'Content',announcements:'AnnouncementsVtml',updates:'UpdatesVtml',events:'EventsVtml',rules:'RulesVtml',serverinfo:'ServerInfoVtml',commands:'CommandsVtml'};
function motdBool(id){return !!$(id)?.checked;}
function motdInt(id,fallback){const n=Number($(id)?.value);return Number.isFinite(n)?Math.trunc(n):fallback;}
function stashCommunityMotdEditor(){
  if(!communityMotdConfig)return;
  const key=COMMUNITY_MOTD_CONTENT_KEYS[communityMotdContentTab]||'Content';
  communityMotdConfig[key]=String($('communityMotdEditor')?.value||'');
  if(key==='Content')communityMotdConfig.ContentLines=[];
}
function selectCommunityMotdContentTab(tab){
  if(!COMMUNITY_MOTD_CONTENT_KEYS[tab])return;
  stashCommunityMotdEditor();
  communityMotdContentTab=tab;
  document.querySelectorAll('#communityMotdContentTabs button[data-motd-content]').forEach(b=>b.classList.toggle('active',b.dataset.motdContent===tab));
  const key=COMMUNITY_MOTD_CONTENT_KEYS[tab];
  if($('communityMotdEditor'))$('communityMotdEditor').value=String(communityMotdConfig?.[key]||'');
  if($('motdNewsBuilder'))$('motdNewsBuilder').hidden=!['announcements','updates'].includes(tab);
  updateCommunityMotdDraft();
  previewCommunityMotd();
}
function renderCommunityMotd(){
  const d=communityMotdData||{},available=!!d.available,c=communityMotdConfig||{};
  if($('communityMotdMissing'))$('communityMotdMissing').style.display=available?'none':'block';
  if($('communityMotdState'))$('communityMotdState').textContent=available?(d.message||'Companion connected'):(d.message||'Companion unavailable');
  if($('communityMotdStatus'))$('communityMotdStatus').innerHTML=
    metric('Companion',available?'CONNECTED':'MISSING',available?'good':'bad')+
    metric('Version',d.version||'—')+
    metric('Format',(Number(c.Format)===1||String(c.Format).toLowerCase()==='markdown')?'Markdown':'VTML')+
    metric('Join behavior',c.OpenOnEveryJoin?'Every join':c.OnlyOncePerSession?'Once / session':c.OnlyOncePerVersion?'Once / version':'Manual');
  const set=(id,v)=>{if($(id))$(id).value=v??'';}, check=(id,v)=>{if($(id))$(id).checked=!!v;};
  set('communityMotdTitle',c.Title||'');set('communityMotdSubtitle',c.Subtitle||'');
  set('communityMotdWidth',c.Width??980);set('communityMotdHeight',c.Height??720);set('communityMotdAccent',c.AccentColor||'#FFC857');
  check('communityMotdLogoEnabled',c.EnableLogo);set('communityMotdLogoSource',c.LogoSource||'Asset');set('communityMotdLogoAsset',c.LogoAsset||'');set('communityMotdServerLogoFile',c.ServerLogoFile||'branding/motd-logo.png');set('communityMotdLogoWidth',c.LogoWidth??180);set('communityMotdLogoHeight',c.LogoHeight??0);check('communityMotdLogoPreserveAspect',c.LogoPreserveAspectRatio!==false);
  check('communityMotdLinkEnabled',c.EnableLinkButton);set('communityMotdLinkText',c.LinkButtonText||'');set('communityMotdLinkUrl',c.LinkUrl||'');check('communityMotdWebMapEnabled',c.EnableWebMapButton);set('communityMotdWebMapText',c.WebMapButtonText||'Web Map');set('communityMotdWebMapUrl',c.WebMapUrl||'');set('communityMotdFooter',c.FooterText||'');
  check('communityMotdEveryJoin',c.OpenOnEveryJoin);check('communityMotdOnceSession',c.OnlyOncePerSession);check('communityMotdOnceVersion',c.OnlyOncePerVersion);check('communityMotdDontShow',c.EnableDontShowAgain);
  set('communityMotdVersionLabel',c.VersionLabel||'');
  if($('communityMotdFormat'))$('communityMotdFormat').value=(Number(c.Format)===1||String(c.Format).toLowerCase()==='markdown')?'Markdown':'VTML';
  check('communityMotdAnnouncementsTab',c.EnableAnnouncementsTab);check('communityMotdUpdatesTab',c.EnableUpdatesTab);check('communityMotdEventsTab',c.EnableEventsTab);check('communityMotdRulesTab',c.EnableRulesTab);check('communityMotdServerTab',c.EnableServerTab);check('communityMotdCommandsTab',c.EnableCommandsTab);
  check('communityMotdDiscordAnnouncements',c.UseDiscordAnnouncements);check('communityMotdDiscordUpdates',c.UseDiscordUpdates);check('communityMotdDiscordEvents',c.UseDiscordEvents);check('communityMotdMergeLocal',c.MergeLocalAndDiscord);check('communityMotdSuiteStatus',c.UseSuiteStatus);check('communityMotdServerServerInfo',c.ServerInfoShowServer!==false);check('communityMotdServerWorldInfo',c.ServerInfoShowWorld!==false);check('communityMotdServerGameplay',c.ServerInfoShowGameplay!==false);check('communityMotdServerProspecting',c.ServerInfoShowProspecting!==false);check('communityMotdServerTemporal',c.ServerInfoShowTemporal!==false);check('communityMotdServerMods',c.ServerInfoShowMods!==false);set('communityMotdDiscordMax',c.DiscordMaxItemsPerFeed??10);
  if($('communityMotdEditor'))$('communityMotdEditor').value=String(c[COMMUNITY_MOTD_CONTENT_KEYS[communityMotdContentTab]]||'');
  document.querySelectorAll('#communityMotdContentTabs button[data-motd-content]').forEach(b=>b.classList.toggle('active',b.dataset.motdContent===communityMotdContentTab));
  const ro=currentSessionRole==='ReadOnly'||!available;
  document.querySelectorAll('#communityMotd input,#communityMotd textarea,#communityMotd select').forEach(e=>e.disabled=ro);
  if($('communityMotdSave'))$('communityMotdSave').disabled=ro;
  if($('communityMotdReload'))$('communityMotdReload').disabled=ro;
  if($('communityMotdResetSeen'))$('communityMotdResetSeen').disabled=ro;
  if($('communityMotdRefresh'))$('communityMotdRefresh').disabled=false;
  previewCommunityMotd();
  updateCommunityMotdDraft();
}
function readCommunityMotdForm(){
  stashCommunityMotdEditor();
  const c=Object.assign({},communityMotdConfig||{});
  c.Title=String($('communityMotdTitle')?.value||'').trim();c.Subtitle=String($('communityMotdSubtitle')?.value||'').trim();
  c.Width=clamp(motdInt('communityMotdWidth',980),700,1600);c.Height=clamp(motdInt('communityMotdHeight',720),520,1000);c.AccentColor=String($('communityMotdAccent')?.value||'#FFC857');
  c.EnableLogo=motdBool('communityMotdLogoEnabled');c.LogoSource=String($('communityMotdLogoSource')?.value||'Asset');c.LogoAsset=String($('communityMotdLogoAsset')?.value||'').trim();c.ServerLogoFile=String($('communityMotdServerLogoFile')?.value||'branding/motd-logo.png').trim();c.LogoWidth=clamp(motdInt('communityMotdLogoWidth',180),0,400);c.LogoHeight=clamp(motdInt('communityMotdLogoHeight',0),0,400);c.LogoPreserveAspectRatio=motdBool('communityMotdLogoPreserveAspect');
  c.EnableLinkButton=motdBool('communityMotdLinkEnabled');c.LinkButtonText=String($('communityMotdLinkText')?.value||'').trim();c.LinkUrl=String($('communityMotdLinkUrl')?.value||'').trim();c.EnableWebMapButton=motdBool('communityMotdWebMapEnabled');c.WebMapButtonText=String($('communityMotdWebMapText')?.value||'Web Map').trim();c.WebMapUrl=String($('communityMotdWebMapUrl')?.value||'').trim();c.FooterText=String($('communityMotdFooter')?.value||'').trim();
  c.OpenOnEveryJoin=motdBool('communityMotdEveryJoin');c.OnlyOncePerSession=motdBool('communityMotdOnceSession');c.OnlyOncePerVersion=motdBool('communityMotdOnceVersion');c.EnableDontShowAgain=motdBool('communityMotdDontShow');
  c.EnablePlayersTab=false;c.VersionLabel=String($('communityMotdVersionLabel')?.value||'').trim();
  c.Format=$('communityMotdFormat')?.value==='Markdown'?1:0;
  c.EnableAnnouncementsTab=motdBool('communityMotdAnnouncementsTab');c.EnableUpdatesTab=motdBool('communityMotdUpdatesTab');c.EnableEventsTab=motdBool('communityMotdEventsTab');c.EnableRulesTab=motdBool('communityMotdRulesTab');c.EnableServerTab=motdBool('communityMotdServerTab');c.EnableCommandsTab=motdBool('communityMotdCommandsTab');c.EnableCommunityTab=false;
  c.UseDiscordAnnouncements=motdBool('communityMotdDiscordAnnouncements');c.UseDiscordUpdates=motdBool('communityMotdDiscordUpdates');c.UseDiscordEvents=motdBool('communityMotdDiscordEvents');c.MergeLocalAndDiscord=motdBool('communityMotdMergeLocal');c.UseSuiteStatus=motdBool('communityMotdSuiteStatus');c.ServerInfoShowServer=motdBool('communityMotdServerServerInfo');c.ServerInfoShowWorld=motdBool('communityMotdServerWorldInfo');c.ServerInfoShowGameplay=motdBool('communityMotdServerGameplay');c.ServerInfoShowProspecting=motdBool('communityMotdServerProspecting');c.ServerInfoShowTemporal=motdBool('communityMotdServerTemporal');c.ServerInfoShowMods=motdBool('communityMotdServerMods');c.DiscordMaxItemsPerFeed=clamp(motdInt('communityMotdDiscordMax',10),1,50);
  if(c.Content!=null)c.ContentLines=[];
  return c;
}
function acceptCommunityMotdBaseline(){
  communityMotdBaseline=JSON.parse(JSON.stringify(communityMotdConfig||{}));
  communityMotdBaselineForm=JSON.stringify(readCommunityMotdForm());
  communityMotdDirty=false;communityMotdRevision++;updateCommunityMotdDraft();
}
function updateCommunityMotdDraft(){
  if(!communityMotdConfig)return;
  communityMotdDirty=!!communityMotdBaseline&&JSON.stringify(readCommunityMotdForm())!==communityMotdBaselineForm;
  const state=$('communityMotdDraftState');if(state){state.textContent=communityMotdBusy?'Applying…':communityMotdDirty?'Unsaved changes · automatic refresh paused':'No unsaved changes';state.classList.toggle('dirty',communityMotdDirty);}
  const source=String($('communityMotdEditor')?.value||'');if($('communityMotdEditorCount'))$('communityMotdEditorCount').textContent=`${source.length.toLocaleString()} characters · ${source.split('\n').length.toLocaleString()} lines`;
  const markdown=communityMotdContentTab==='main'&&$('communityMotdFormat')?.value==='Markdown';
  if($('communityMotdEditorFormat'))$('communityMotdEditorFormat').textContent=markdown?'Markdown':'VTML';
  if($('communityMotdFormat'))$('communityMotdFormat').title=communityMotdContentTab==='main'?'Main content format':'This setting applies to Main only. The selected tab uses VTML.';
  const ro=currentSessionRole==='ReadOnly'||!communityMotdData?.available||communityMotdBusy;
  document.querySelectorAll('#communityMotd input,#communityMotd textarea,#communityMotd select,#communityMotd [data-motd-insert]').forEach(e=>e.disabled=ro);
  if($('motdPostInsert'))$('motdPostInsert').disabled=ro;
  if($('motdNewsBuilder'))$('motdNewsBuilder').hidden=!['announcements','updates'].includes(communityMotdContentTab);
  if($('communityMotdSave'))$('communityMotdSave').disabled=ro||!communityMotdDirty;
  if($('communityMotdDiscard'))$('communityMotdDiscard').disabled=ro||!communityMotdDirty;
  if($('communityMotdReload'))$('communityMotdReload').disabled=ro;
  if($('communityMotdResetSeen'))$('communityMotdResetSeen').disabled=ro;
  document.querySelectorAll('#communityMotdContentTabs [data-motd-content]').forEach(button=>{const key=COMMUNITY_MOTD_CONTENT_KEYS[button.dataset.motdContent];button.classList.toggle('modified',!!communityMotdBaseline&&String(communityMotdConfig[key]||'')!==String(communityMotdBaseline[key]||''));});
}
function editCommunityMotd(){
  communityMotdRevision++;updateCommunityMotdDraft();clearTimeout(motdPreviewTimer);motdPreviewTimer=setTimeout(previewCommunityMotd,100);
}
function formatMotdNewsPost(title,body,date,important){
 const safe=value=>String(value).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');
 const lines=String(body).replace(/\r\n?/g,'\n').trim().split('\n');
 const content=lines.map(line=>safe(line.replace(/^\s*[-*]\s+/, '• '))).join('\n');
 const flag=important?'<font size="11" color="#EDBD78"><strong>IMPORTANT</strong></font>\n':'';
 const stamp=/^\d{4}-\d{2}-\d{2}$/.test(date)?'<font size="11" color="#99ABB6">'+safe(date)+' · Server team</font>\n':'<font size="11" color="#99ABB6">Server team</font>\n';
 return flag+'<font size="17" color="#D9B777"><strong>'+safe(title.trim())+'</strong></font>\n'+stamp+'\n'+content+'\n\n';
}
function insertMotdNewsPost(){
 if(currentSessionRole==='ReadOnly'||communityMotdBusy||!communityMotdData?.available||!['announcements','updates'].includes(communityMotdContentTab))return;
 const title=$('motdPostTitle').value.trim(),body=$('motdPostBody').value.trim(),date=$('motdPostDate').value;
 if(!title||!body){toast('Enter a post title and text.','error');return;}
 const editor=$('communityMotdEditor');
 const previous=editor.value.trim();
 const separator=previous?'<font size="11" color="#52616C">────────────────────────</font>\n\n':'';
 editor.value=formatMotdNewsPost(title,body,date,$('motdPostImportant').checked)+separator+previous;
 $('motdPostTitle').value='';$('motdPostBody').value='';$('motdPostImportant').checked=false;
 stashCommunityMotdEditor();editCommunityMotd();previewCommunityMotd();toast('Post added to your draft. Save to publish.','success');
}
function insertCommunityMotdFormatting(kind){
  if(currentSessionRole==='ReadOnly'||!communityMotdData?.available||communityMotdBusy)return;
  const editor=$('communityMotdEditor');if(!editor)return;
  const start=editor.selectionStart,end=editor.selectionEnd,selected=editor.value.slice(start,end),markdown=communityMotdContentTab==='main'&&$('communityMotdFormat')?.value==='Markdown';
  let before='',after='',text=selected||'text';
  if(kind==='bold'){before=markdown?'**':'<strong>';after=markdown?'**':'</strong>';}
  if(kind==='italic'){before=markdown?'*':'<i>';after=markdown?'*':'</i>';}
  if(kind==='heading'){before=markdown?'## ':'<font size="20"><strong>';after=markdown?'':'</strong></font>';text=selected||'Heading';}
  if(kind==='code'){before=markdown?'`':'<code>';after=markdown?'`':'</code>';}
  if(kind==='color'){before=`<font color="${$('communityMotdTextColor').value}">`;after='</font>';}
  if(kind==='bullet'){before='• ';text=selected?selected.replace(/\n/g,'\n• '):'List item';}
  const replacement=before+text+after;
  editor.focus();editor.setRangeText(replacement,start,end,'end');editor.setSelectionRange(start+before.length,start+before.length+text.length);editCommunityMotd();
}
function motdPreviewMarkup(source,markdown){
  if(!markdown)return source;
  return source.replace(/^\s*(#{1,6})\s+(.*)$/gm,(_,hash,text)=>`<font size="${24-hash.length*2}"><strong>${text}</strong></font>`)
    .replace(/\*\*(.+?)\*\*/g,'<strong>$1</strong>').replace(/\*(.+?)\*/g,'<i>$1</i>')
    .replace(/`(.+?)`/g,'<code>$1</code>').replace(/\[(.+?)\]\((.+?)\)/g,'$1');
}
function previewCommunityMotd(){
  const body=$('communityMotdPreviewBody');if(!body)return;
  $('communityMotdPreviewTitle').textContent=$('communityMotdTitle')?.value||'Server MOTD';
  $('communityMotdPreviewSubtitle').textContent=$('communityMotdSubtitle')?.value||'';
  $('communityMotdPreviewFooter').textContent=$('communityMotdFooter')?.value||'';
  $('communityMotdPreview').style.setProperty('--motd-accent',$('communityMotdAccent')?.value||'#ffc857');
  const source=String($('communityMotdEditor')?.value||''),markdown=communityMotdContentTab==='main'&&$('communityMotdFormat')?.value==='Markdown';
  // Template contents stay inert. Copy only text and supported formatting into live DOM;
  // no original elements, event handlers, resource URLs or navigation attributes survive.
  const template=document.createElement('template');template.innerHTML=motdPreviewMarkup(source.slice(0,50000),markdown);
  body.replaceChildren();let nodes=0;
  function copy(node,parent,depth){
    if(++nodes>10000||depth>40)return;
    if(node.nodeType===Node.TEXT_NODE){parent.appendChild(document.createTextNode(node.textContent));return;}
    if(node.nodeType!==Node.ELEMENT_NODE)return;
    const tag=node.tagName.toLowerCase();if(['script','style','img','iframe','object','embed','link','meta','svg'].includes(tag))return;
    if(tag==='br'){parent.appendChild(document.createElement('br'));return;}
    let target=parent;
    if(['strong','b','i','em','code','font','u','a'].includes(tag)){
      target=document.createElement(tag==='font'||tag==='u'||tag==='a'?'span':tag);
      if(tag==='font'){
        const color=node.getAttribute('color')||'',size=Number(node.getAttribute('size'));
        if(/^#[0-9a-f]{3,8}$/i.test(color))target.style.color=color;
        if(Number.isFinite(size)&&size>0)target.style.fontSize=Math.max(10,Math.min(32,size))+'px';
      }
      if(tag==='u'||tag==='a')target.style.textDecoration='underline';
      parent.appendChild(target);
    }
    Array.from(node.childNodes).forEach(child=>copy(child,target,depth+1));
  }
  Array.from(template.content.childNodes).forEach(node=>copy(node,body,0));
  if(!source){const empty=document.createElement('span');empty.className='muted';empty.textContent='This tab has no local content yet.';body.appendChild(empty);}
  if(source.length>50000||nodes>10000){const note=document.createElement('div');note.className='small';note.textContent='Preview limited for large content; the full source is retained.';body.appendChild(note);}
}
function revertCommunityMotdDraft(){
  if(communityMotdBusy||currentSessionRole==='ReadOnly'||!communityMotdBaseline)return;
  if(communityMotdDirty&&!confirm('Revert all unsaved MOTD settings and content to the last loaded/saved version?'))return;
  communityMotdRevision++;communityMotdConfig=JSON.parse(JSON.stringify(communityMotdBaseline));renderCommunityMotd();
}
async function refreshCommunityMotdDraft(){
  if(communityMotdBusy)return;
  if(communityMotdDirty&&!confirm('Discard unsaved MOTD changes and refresh from the server?'))return;
  await loadCommunityMotd(true,true);
}

async function loadCommunityMotd(showLoading=true,replaceDraft=false){
  if(communityMotdLoading||communityMotdBusy||(communityMotdDirty&&!replaceDraft))return;
  communityMotdLoading=true;const revision=communityMotdRevision;
  if(showLoading&&$('communityMotdState'))$('communityMotdState').textContent='Checking companion…';
  try{
    const d=await api('/api/community-motd');
    if(revision!==communityMotdRevision||communityMotdBusy||(communityMotdDirty&&!replaceDraft))return;
    communityMotdData=d;
    try{communityMotdConfig=JSON.parse(d.configJson||'{}')||{};}catch{communityMotdConfig={};}
    if(Array.isArray(communityMotdConfig.ContentLines)&&communityMotdConfig.ContentLines.length&&!String(communityMotdConfig.Content||'').trim())communityMotdConfig.Content=communityMotdConfig.ContentLines.join('\n');
    renderCommunityMotd();acceptCommunityMotdBaseline();
  }catch(e){
    if(!communityMotdBaseline){communityMotdData={available:false,message:e.message};communityMotdConfig={};renderCommunityMotd();}
    if($('communityMotdState'))$('communityMotdState').textContent='Refresh failed; existing content retained.';
    if(showLoading)toast(e.message,'error');
  }finally{communityMotdLoading=false;updateCommunityMotdDraft();}
}
async function saveCommunityMotd(){
  if(currentSessionRole==='ReadOnly'||!communityMotdData?.available||communityMotdBusy)return;
  const config=readCommunityMotdForm();communityMotdBusy=true;updateCommunityMotdDraft();
  try{
    const d=await api('/api/community-motd',{method:'POST',body:JSON.stringify({action:'save',config})});
    communityMotdData=d;try{communityMotdConfig=JSON.parse(d.configJson||JSON.stringify(config))||config;}catch{communityMotdConfig=config;}
    renderCommunityMotd();acceptCommunityMotdBaseline();toast(d.message||'Community MOTD saved and applied.','success');
  }catch(e){toast(e.message,'error');}
  finally{communityMotdBusy=false;updateCommunityMotdDraft();}
}
async function communityMotdAction(action){
  if(currentSessionRole==='ReadOnly'||!communityMotdData?.available||communityMotdBusy)return;
  if(action==='resetseen'&&!confirm('Reset the Community MOTD seen-state for all connected clients?'))return;
  if(action==='reload'&&communityMotdDirty&&!confirm('Discard unsaved MOTD changes and reload the saved configuration from disk?'))return;
  communityMotdBusy=true;updateCommunityMotdDraft();
  try{
    const d=await api('/api/community-motd',{method:'POST',body:JSON.stringify({action})});
    communityMotdData=d;
    // Reset Seen does not edit configuration and must not discard a local draft.
    if(action==='reload'){try{communityMotdConfig=JSON.parse(d.configJson||'{}')||communityMotdConfig;}catch{}renderCommunityMotd();acceptCommunityMotdBaseline();}
    toast(d.message||'Community MOTD action completed.','success');
  }catch(e){toast(e.message,'error');}
  finally{communityMotdBusy=false;updateCommunityMotdDraft();}
}
