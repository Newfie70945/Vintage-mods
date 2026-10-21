namespace BlockLogger.WebDashboard;

internal static class WebDashboardPlayerCommandsScript
{
    public const string Script = """
let playerCommandsState=null;
let playerCommandsBaseline='',playerCommandsDirty=false,playerCommandsBusy=false,playerCommandsLoading=false;
let playerCommandsActiveTab='commands',playerPermissionBusy=false;
const PLAYER_COMMAND_DRAFT_SELECTOR='#playercommands input[id^="playerCmd"],#playercommands select[id^="playerCmd"],#playercommands textarea[id^="playerCmd"],#playercommands [data-role-policy-mode],#playercommands [data-role-policy-access],#playercommands [data-role-policy-cooldown]';
function playerCommandDraftFields(){return [...document.querySelectorAll(PLAYER_COMMAND_DRAFT_SELECTOR)].filter(e=>e.id!=='playerCmdRoleCommand');}
function playerCommandDraftSignature(){return JSON.stringify(playerCommandDraftFields().map(e=>[e.id||`${e.dataset.role}:${e.dataset.rolePolicyMode||e.dataset.rolePolicyAccess||e.dataset.rolePolicyCooldown}:${e.type}`,e.type==='checkbox'?e.checked:e.value]));}
function finishPlayerCommandsRender(){
  playerCommandsBaseline=playerCommandDraftSignature();playerCommandsDirty=false;
  syncPlayerCommandsDraftControls();
}
function updatePlayerCommandsDirty(){
  if(!playerCommandsState?.available||playerCommandsBusy||playerCommandsLoading)return;
  playerCommandsDirty=playerCommandDraftSignature()!==playerCommandsBaseline;
  syncPlayerCommandsDraftControls();
}
function syncPlayerCommandsDraftControls(){
  const pending=playerCommandsBusy||playerCommandsLoading;
  const ro=currentSessionRole==='ReadOnly',available=playerCommandsState?.available===true;
  const blocked=ro||!available||pending;
  playerCommandDraftFields().forEach(el=>el.disabled=blocked);
  if(!blocked){$('playerCmdReplyEnabled').disabled=!$('playerCmdMsgEnabled').checked;$('playerCmdWarpEnabled').disabled=!$('playerCmdWarpsEnabled').checked;}
  syncPlayerCommandRoleControls();
  $('playerCommandsSave').disabled=blocked||!playerCommandsDirty;
  $('playerCommandsSave').textContent=playerCommandsBusy?'Applying…':'Save Settings';
  $('playerCommandsDiscard').disabled=pending||!playerCommandsDirty;
  $('playerCommandsRefresh').disabled=pending;
  ['playerCommandsEnableAll','playerCommandsDisableAll','playerCmdRolePolicyReset'].forEach(id=>$(id).disabled=blocked);
  document.querySelectorAll('[data-player-command-toggle]').forEach(el=>el.disabled=blocked||playerCommandsDirty);
  $('playerCommandsDraftStatus').textContent=playerCommandsBusy?'Applying changes…':playerCommandsLoading?'Loading settings…':!available?'Settings unavailable':ro?'Read-only account':playerCommandsDirty?'Unsaved settings — save to apply':'All settings saved';
  $('playerCommandsDraftStatus').className=playerCommandsDirty?'warn':'small';
  $('playerCommandsDraftStatus').closest('.playerCommandSaveBar').classList.toggle('hasEdits',playerCommandsDirty);
}
function discardPlayerCommandsDraft(){
  if(playerCommandsBusy||playerCommandsLoading||!playerCommandsDirty)return;
  if(!confirm('Discard unsaved Player Commands settings?'))return;
  renderPlayerCommands(playerCommandsState);
}
async function reloadPlayerCommandsDraft(){
  if(playerCommandsBusy||playerCommandsLoading)return;
  if(playerCommandsDirty){
    if(!confirm('Discard unsaved Player Commands settings and reload from the server?'))return;
    renderPlayerCommands(playerCommandsState);
  }
  await loadPlayerCommands(true);
}
function setPlayerCommandTab(tab){
  const tabs=[...document.querySelectorAll('[data-player-command-tab]')];
  if(!tabs.some(b=>b.dataset.playerCommandTab===tab))return;
  playerCommandsActiveTab=tab;
  tabs.forEach(b=>{const selected=b.dataset.playerCommandTab===tab;b.classList.toggle('active',selected);b.setAttribute('aria-selected',String(selected));b.tabIndex=selected?0:-1;$(b.getAttribute('aria-controls')).hidden=!selected;});
  if(tab==='permissions'&&!playerPermissionState)loadPlayerPermissions(false);
  if(currentSection==='playercommands')setTimeout(syncWorkspacePage,0);
}
function renderPlayerCommandDirectory(){
  const commands=Array.isArray(playerCommandsState?.commands)?playerCommandsState.commands:[];
  const query=$('playerCommandSearch').value.trim().toLowerCase(),filter=$('playerCommandFilter').value;
  const shown=commands.filter(c=>{
    const external=!c.rootOwnedBySuite&&c.rootProvider&&c.rootProvider!=='Unregistered';
    return (!query||[c.command,c.fallbackCommand,c.description,c.requiredPrivilege,c.rootProvider].join(' ').toLowerCase().includes(query))&&(filter==='all'||filter==='enabled'&&c.enabled||filter==='disabled'&&!c.enabled||filter==='external'&&external);
  });
  $('playerCommandResultCount').textContent=`${shown.length} of ${commands.length} commands`;
  $('playerCommandsRows').innerHTML=shown.map(c=>{
    const individual=c.individuallyEnabled!==false,provider=String(c.rootProvider||'Unknown');
    const external=!c.rootOwnedBySuite&&provider!=='Unregistered';
    const label=c.enabled?'Command enabled':individual?'Feature or provider off':'Command disabled';
    return `<article class="playerCommandEntry"><div class="playerCommandEntryHead"><code>${esc(c.command||'')}</code><span class="badge ${c.enabled?'low':'medium'}">${label}</span></div><p>${esc(c.description||'')}</p><dl class="playerCommandFacts"><dt>Root alias</dt><dd class="${playerCommandProviderTone(provider)}">${esc(provider)}</dd><dt>Fallback</dt><dd><code>${esc(c.fallbackCommand||'—')}</code></dd><dt>Privilege</dt><dd><code>${esc(c.requiredPrivilege||'chat')}</code></dd></dl><div class="playerCommandEntryFoot"><span class="small">${external?'Changes affect the Suite fallback.':'Individual switch applies now.'}</span><button data-player-command-toggle="${esc(c.key||'')}" data-command-enabled="${individual}" aria-label="${individual?'Disable':'Enable'} command ${esc(c.command||'')}"${individual?'':' class="primary"'}>${individual?'Disable command':'Enable command'}</button></div></article>`;
  }).join('')||'<div class="empty">No commands match these filters.</div>';
  document.querySelectorAll('[data-player-command-toggle]').forEach(b=>b.onclick=()=>togglePlayerCommand(b.dataset.playerCommandToggle,b.dataset.commandEnabled!=='true'));
  syncPlayerCommandsDraftControls();
}
function revealPlayerCommandField(el){
  const panel=el.closest('.playerCommandPanel');if(panel)setPlayerCommandTab(panel.id.replace('playerCommandPanel-',''));
  const roleCmd=el.dataset.rolePolicyCooldown||el.dataset.rolePolicyMode||el.dataset.rolePolicyAccess;
  if(roleCmd){$('playerCmdRoleCommand').value=roleCmd;filterPlayerCommandRoleColumns();}
  const card=el.closest('.workspaceCard');if(card)setWorkspaceCardCollapsed(card,false);
  el.focus();el.scrollIntoView({block:'center',behavior:'smooth'});
}
function validatePlayerCommandsDraft(){
  for(const el of playerCommandDraftFields().filter(e=>e.type==='number'&&!e.disabled)){
    if(el.value.trim()===''||!Number.isInteger(Number(el.value))||!el.checkValidity()){
      revealPlayerCommandField(el);el.reportValidity();throw new Error('Enter a whole number within the displayed limits.');
    }
  }
  for(const id of ['playerCmdHomeRoleLimits','playerCmdHomePlayerLimits']){
    try{playerCommandLimitTextToMap($(id).value);}catch(e){revealPlayerCommandField($(id));throw e;}
  }
  if(Number($('playerCmdRtpMaxRadius').value)<Number($('playerCmdRtpMinRadius').value)){
    revealPlayerCommandField($('playerCmdRtpMaxRadius'));throw new Error('RTP maximum radius must be at least the minimum radius.');
  }
  if($('playerCmdRtpCostEnabled').checked&&!$('playerCmdRtpCostItem').value.trim()){
    revealPlayerCommandField($('playerCmdRtpCostItem'));throw new Error('Set an item code before enabling an RTP cost.');
  }
}
function filterPlayerPermissionRows(){
  const query=($('playerPermissionSearch')?.value||'').trim().toLowerCase();
  const rows=[...document.querySelectorAll('#playerPermPlayerRows tr[data-player-permission-search]')];
  rows.forEach(row=>row.hidden=!!query&&!row.dataset.playerPermissionSearch.includes(query));
  if($('playerPermissionSearchCount'))$('playerPermissionSearchCount').textContent=`${rows.filter(r=>!r.hidden).length} of ${rows.length} online players`;
}


function playerCommandProviderTone(provider){
  const p=String(provider||'').toLowerCase();
  if(p.includes("newfie's server essentials"))return 'good';
  if(p.includes('th3essentials'))return 'warn';
  if(p.includes('another'))return 'warn';
  return 'neutral';
}

function playerCommandLimitMapToText(obj){
  return Object.entries(obj||{}).sort((a,b)=>String(a[0]).localeCompare(String(b[0]))).map(([k,v])=>`${k}=${Number(v||0)}`).join('\n');
}
function playerCommandLimitTextToMap(text){
  const out={};
  String(text||'').split(/\r?\n|,/).forEach((raw,index)=>{
    const line=raw.trim();if(!line||line.startsWith('#'))return;
    const match=line.match(/^([^=]+)=\s*(\d+)\s*$/);
    if(!match||Number(match[2])>100)throw new Error(`Home limit line ${index+1}: use name=limit with a whole number from 0 to 100.`);
    const key=match[1].trim();
    if(Object.keys(out).some(k=>k.toLowerCase()===key.toLowerCase()))throw new Error(`Duplicate home limit for ${key}.`);
    Object.defineProperty(out,key,{value:Number(match[2]),enumerable:true,configurable:true,writable:true});
  });
  return out;
}

const TELEPORT_ROLE_COMMANDS=['spawn','home','back','warp','t2p','t2m','rtp'];
function rolePolicyFor(d,command,role){
  const map=d?.teleportRolePolicies?.[command]||{};
  return map[role]||map[String(role||'').toLowerCase()]||null;
}
function defaultTeleportRoleAccess(role,command){
  if(command!=='t2p'&&command!=='t2m')return true;
  const privs=Array.isArray(role?.privileges)?role.privileges.map(x=>String(x||'').toLowerCase()):[];
  return privs.includes('root')||privs.includes('controlserver');
}
function renderTeleportRolePolicies(d){
  const host=$('playerCmdRolePolicyRows');if(!host)return;
  const roles=(Array.isArray(d?.availableRoles)?d.availableRoles:[]).map(r=>({...r,code:String(r.code||'').toLowerCase()}));
  const seen=new Set(roles.map(r=>r.code));
  for(const map of Object.values(d?.teleportRolePolicies||{}))for(const code of Object.keys(map||{})){
    if(!seen.has(code.toLowerCase())){roles.push({code:code.toLowerCase(),name:code==='*'?'All roles (*)':`${code} (saved policy)`,privileges:[]});seen.add(code.toLowerCase());}
  }
  if(!roles.length){host.innerHTML='<tr><td colspan="4" class="muted">No server roles were returned.</td></tr>';return;}
  host.innerHTML=roles.map(r=>{
    const code=r.code;
    const cells=TELEPORT_ROLE_COMMANDS.map(cmd=>{
      const pol=rolePolicyFor(d,cmd,code);
      const fallback=code!=='*'?rolePolicyFor(d,cmd,'*'):null;
      const defaultAllowed=fallback?fallback.allowed!==false:defaultTeleportRoleAccess(r,cmd);
      const defaultCooldown=fallback?Number(fallback.cooldownSeconds??-1):-1;
      return `<td data-role-command-cell="${cmd}"><select aria-label="/${cmd} policy for ${esc(code)}" data-role-policy-mode="${cmd}" data-role="${esc(code)}"><option value="inherit"${!pol?' selected':''}>Inherited</option><option value="override"${pol?' selected':''}>Override</option></select></td><td data-role-command-cell="${cmd}"><input type="checkbox" aria-label="Allow /${cmd} for ${esc(code)}" data-role-policy-access="${cmd}" data-role="${esc(code)}" data-default-allowed="${defaultTeleportRoleAccess(r,cmd)?'true':'false'}"${(pol?pol.allowed!==false:defaultAllowed)?' checked':''}></td><td data-role-command-cell="${cmd}"><input type="number" min="-1" max="604800" step="1" required value="${Number(pol?.cooldownSeconds??defaultCooldown)}" aria-label="/${cmd} cooldown for ${esc(code)}" data-role-policy-cooldown="${cmd}" data-role="${esc(code)}"></td>`;
    }).join('');
    return `<tr><td><b>${esc(r.name||code)}</b><br><code>${esc(code)}</code></td>${cells}</tr>`;
  }).join('');
  syncPlayerCommandRoleControls();
  filterPlayerCommandRoleColumns();
}
function syncPlayerCommandRoleControls(){
  const blocked=currentSessionRole==='ReadOnly'||!playerCommandsState?.available||playerCommandsBusy||playerCommandsLoading;
  const modes=[...document.querySelectorAll('[data-role-policy-mode]')];
  modes.forEach(mode=>{
    const cmd=mode.dataset.rolePolicyMode,role=mode.dataset.role;
    const access=[...document.querySelectorAll(`[data-role-policy-access="${cmd}"]`)].find(x=>x.dataset.role===role);
    const cool=[...document.querySelectorAll(`[data-role-policy-cooldown="${cmd}"]`)].find(x=>x.dataset.role===role);
    mode.disabled=blocked;access.disabled=cool.disabled=blocked||mode.value==='inherit';
    if(mode.value==='inherit'){
      const wildcard=modes.find(x=>x.dataset.rolePolicyMode===cmd&&x.dataset.role==='*'&&x.value==='override');
      const wildcardRow=wildcard?.closest('tr');
      access.checked=wildcard&&role!=='*'?wildcardRow.querySelector(`[data-role-policy-access="${cmd}"]`).checked:access.dataset.defaultAllowed==='true';
      cool.value=wildcard&&role!=='*'?wildcardRow.querySelector(`[data-role-policy-cooldown="${cmd}"]`).value:'-1';
    }
  });
}
function filterPlayerCommandRoleColumns(){
  const cmd=$('playerCmdRoleCommand').value;
  document.querySelectorAll('[data-role-command-cell]').forEach(cell=>cell.hidden=cell.dataset.roleCommandCell!==cmd);
}
function collectTeleportRolePolicies(){
  // Keep saved policies for roles absent from the current role list; inheritance
  // must stay inheritance rather than being saved as a new explicit override.
  const out=JSON.parse(JSON.stringify(playerCommandsState?.teleportRolePolicies||{}));
  document.querySelectorAll('[data-role-policy-mode]').forEach(mode=>{
    const cmd=mode.dataset.rolePolicyMode,role=mode.dataset.role;
    out[cmd]??={};
    if(mode.value==='inherit'){delete out[cmd][role];return;}
    const row=mode.closest('tr');
    const access=row.querySelector(`[data-role-policy-access="${cmd}"]`);
    const cool=row.querySelector(`[data-role-policy-cooldown="${cmd}"]`);
    Object.defineProperty(out[cmd],role,{value:{allowed:access.checked,cooldownSeconds:Number(cool.value)},enumerable:true,configurable:true,writable:true});
  });
  Object.keys(out).forEach(cmd=>{if(!Object.keys(out[cmd]).length)delete out[cmd];});
  return out;
}
function resetTeleportRolePolicies(){
  if(currentSessionRole==='ReadOnly'||playerCommandsBusy||!playerCommandsState?.available)return;
  document.querySelectorAll('[data-role-policy-mode]').forEach(e=>e.value='inherit');
  syncPlayerCommandRoleControls();updatePlayerCommandsDirty();
  toast('Role overrides cleared in the draft. Save Settings to apply.','success');
}

function renderPlayerCommands(d){
  playerCommandsState=d||null;
  const available=!!d?.available;
  const th3=!!d?.th3EssentialsDetected;
  const ro=currentSessionRole==='ReadOnly';
  if(!available){
    $('playerCommandsNotice').className='warning';
    $('playerCommandsNotice').innerHTML='<b>Player Commands feature is not running.</b> Enable the <code>playercommands</code> feature gate under Web Server → Suite Feature Gates, then restart the server.';
    $('playerCommandsMetrics').innerHTML=metric('Module','Not running','warn')+metric('Live settings','Unavailable','warn');
    $('playerCommandsRows').innerHTML='<div class="empty">Player Commands service is unavailable.</div>';
    ['playerCmdSpawnEnabled','playerCmdSpawnCooldown','playerCmdSpawnWarmup','playerCmdSpawnCancelMove','playerCmdMsgEnabled','playerCmdReplyEnabled','playerCmdRespectMutes','playerCmdMaxLength','playerCmdStarterEnabled','playerCmdStarterProvider','playerCmdWarpsEnabled','playerCmdWarpEnabled','playerCmdBackEnabled','playerCmdBackDeaths','playerCmdBackTeleports','playerCmdBackCancelMove','playerCmdBackCooldown','playerCmdBackWarmup','playerCmdHomesEnabled','playerCmdHomeCancelMove','playerCmdHomeGlobalLimit','playerCmdHomeCooldown','playerCmdHomeWarmup','playerCmdHomeRoleLimits','playerCmdHomePlayerLimits','playerCmdT2pEnabled','playerCmdT2pCooldown','playerCmdT2mEnabled','playerCmdT2mCooldown','playerCmdRtpEnabled','playerCmdRtpCancelMove','playerCmdRtpAvoidLiquids','playerCmdRtpCenterPlayer','playerCmdRtpCostEnabled','playerCmdRtpMinRadius','playerCmdRtpMaxRadius','playerCmdRtpCooldown','playerCmdRtpWarmup','playerCmdRtpCostItem','playerCmdRtpCostQty','playerCmdRtpAttempts','playerCommandsEnableAll','playerCommandsDisableAll','playerCmdRolePolicyReset','playerCommandsSave'].forEach(id=>{const e=$(id);if(e)e.disabled=true;});
    renderTeleportRolePolicies(d);
    finishPlayerCommandsRender();
    setNavStatus('playercommands','warn','Player Commands feature is not running');
    return;
  }

  const commands=Array.isArray(d.commands)?d.commands:[];
  $('playerCommandsNotice').className=th3?'warning':'infoBox';
  $('playerCommandsNotice').innerHTML=th3
    ? '<b>Th3Essentials compatibility mode is active.</b> NSE deliberately leaves the root <code>/spawn</code>, <code>/msg</code>, <code>/r</code>, <code>/starterkit</code>, and starter-kit administration aliases to Th3Essentials. The Starter Kit Provider setting below controls NSE starter-kit fallback behavior and never disables Th3Essentials commands.'
    : '<b>NSE player commands are active.</b> Free root aliases are registered once at startup; enable/disable changes below are enforced live without a restart.';

  $('playerCommandsMetrics').innerHTML=
    metric('Suite commands',`${commands.filter(c=>c.enabled).length} / ${commands.length} enabled`,'good')+
    metric('Other providers',commands.filter(c=>!c.rootOwnedBySuite&&c.rootProvider!=='Unregistered').length,th3?'warn':'neutral')+
    metric('Teleport policies',Object.values(d.teleportRolePolicies||{}).reduce((sum,map)=>sum+Object.keys(map||{}).length,0),'neutral')+
    metric('Warps',d.warpsEnabled?`${Number(d.warpCount||0)} destinations`:'Disabled',d.warpsEnabled?'good':'neutral');

  $('playerCmdSpawnEnabled').checked=!!d.spawnEnabled;
  $('playerCmdSpawnCooldown').value=Number(d.spawnCooldownSeconds??60);
  $('playerCmdSpawnWarmup').value=Number(d.spawnWarmupSeconds??5);
  $('playerCmdSpawnCancelMove').checked=d.spawnCancelOnMove!==false;
  $('playerCmdMsgEnabled').checked=!!d.privateMessagesEnabled;
  $('playerCmdReplyEnabled').checked=!!d.replyEnabled;
  $('playerCmdRespectMutes').checked=!!d.respectSuiteMutes;
  $('playerCmdMaxLength').value=Number(d.maxPrivateMessageLength??500);
  $('playerCmdStarterEnabled').checked=!!d.starterKitEnabled;
  $('playerCmdStarterProvider').value=String(d.starterKitProvider||'Auto');
  $('playerCmdWarpsEnabled').checked=d.warpsEnabled!==false;
  $('playerCmdWarpEnabled').checked=d.warpCommandEnabled!==false;
  $('playerCmdBackEnabled').checked=d.backEnabled!==false;
  $('playerCmdBackDeaths').checked=d.backTrackDeaths!==false;
  $('playerCmdBackTeleports').checked=d.backTrackTeleports!==false;
  $('playerCmdBackCancelMove').checked=d.backCancelOnMove!==false;
  $('playerCmdBackCooldown').value=Number(d.backCooldownSeconds??30);
  $('playerCmdBackWarmup').value=Number(d.backWarmupSeconds??5);
  $('playerCmdHomesEnabled').checked=d.homesEnabled!==false;
  $('playerCmdHomeCancelMove').checked=d.homeCancelOnMove!==false;
  $('playerCmdHomeGlobalLimit').value=Number(d.homeGlobalLimit??2);
  $('playerCmdHomeCooldown').value=Number(d.homeCooldownSeconds??60);
  $('playerCmdHomeWarmup').value=Number(d.homeWarmupSeconds??5);
  $('playerCmdHomeRoleLimits').value=playerCommandLimitMapToText(d.homeRoleLimits);
  $('playerCmdHomePlayerLimits').value=playerCommandLimitMapToText(d.homePlayerLimits);
  $('playerCmdT2pEnabled').checked=d.t2pEnabled!==false;
  $('playerCmdT2pCooldown').value=Number(d.t2pCooldownSeconds??30);
  $('playerCmdT2mEnabled').checked=d.t2mEnabled!==false;
  $('playerCmdT2mCooldown').value=Number(d.t2mCooldownSeconds??30);
  $('playerCmdRtpEnabled').checked=d.rtpEnabled!==false;
  $('playerCmdRtpCancelMove').checked=d.rtpCancelOnMove!==false;
  $('playerCmdRtpAvoidLiquids').checked=d.rtpAvoidWaterAndLava!==false;
  $('playerCmdRtpCenterPlayer').checked=!!d.rtpCenterOnPlayerPosition;
  $('playerCmdRtpCostEnabled').checked=!!d.rtpCostEnabled;
  $('playerCmdRtpMinRadius').value=Number(d.rtpMinRadius??1000);
  $('playerCmdRtpMaxRadius').value=Number(d.rtpMaxRadius??10000);
  $('playerCmdRtpCooldown').value=Number(d.rtpCooldownSeconds??1800);
  $('playerCmdRtpWarmup').value=Number(d.rtpWarmupSeconds??10);
  $('playerCmdRtpCostItem').value=String(d.rtpCostItemCode||'game:gear-rusty');
  $('playerCmdRtpCostQty').value=Number(d.rtpCostQuantity??1);
  $('playerCmdRtpAttempts').value=Number(d.rtpMaxSearchAttempts??20);
  renderTeleportRolePolicies(d);
  $('playerCmdStarterStatus').textContent=`Effective: ${d.starterKitEffectiveProvider||'NSE'} · ${d.starterKitItemCount||0} item stack(s) · ${d.starterKitClaimedCount||0} claim(s)`;

  renderPlayerCommandDirectory();

  ['playerCmdSpawnEnabled','playerCmdSpawnCooldown','playerCmdSpawnWarmup','playerCmdSpawnCancelMove','playerCmdMsgEnabled','playerCmdReplyEnabled','playerCmdRespectMutes','playerCmdMaxLength','playerCmdStarterEnabled','playerCmdStarterProvider','playerCmdWarpsEnabled','playerCmdWarpEnabled','playerCmdBackEnabled','playerCmdBackDeaths','playerCmdBackTeleports','playerCmdBackCancelMove','playerCmdBackCooldown','playerCmdBackWarmup','playerCmdHomesEnabled','playerCmdHomeCancelMove','playerCmdHomeGlobalLimit','playerCmdHomeCooldown','playerCmdHomeWarmup','playerCmdHomeRoleLimits','playerCmdHomePlayerLimits','playerCmdT2pEnabled','playerCmdT2pCooldown','playerCmdT2mEnabled','playerCmdT2mCooldown','playerCmdRtpEnabled','playerCmdRtpCancelMove','playerCmdRtpAvoidLiquids','playerCmdRtpCenterPlayer','playerCmdRtpCostEnabled','playerCmdRtpMinRadius','playerCmdRtpMaxRadius','playerCmdRtpCooldown','playerCmdRtpWarmup','playerCmdRtpCostItem','playerCmdRtpCostQty','playerCmdRtpAttempts','playerCommandsEnableAll','playerCommandsDisableAll','playerCmdRolePolicyReset','playerCommandsSave'].forEach(id=>{const e=$(id);if(e)e.disabled=ro;});
  $('playerCmdReplyEnabled').disabled=ro||!$('playerCmdMsgEnabled').checked;
  $('playerCmdWarpEnabled').disabled=ro||!$('playerCmdWarpsEnabled').checked;
  finishPlayerCommandsRender();
  setNavStatus('playercommands',th3?'warn':'good',th3?'NSE yielded root player-command aliases for Th3Essentials compatibility':'NSE player commands are available');
}

async function loadPlayerCommands(showLoading=true){
  if(playerCommandsDirty||playerCommandsBusy||playerCommandsLoading)return;
  playerCommandsLoading=true;syncPlayerCommandsDraftControls();
  if(showLoading&&$('playerCommandsNotice'))$('playerCommandsNotice').textContent='Loading player command status…';
  try{
    const d=await api('/api/playercommands');
    renderPlayerCommands(d);
    if(playerCommandsActiveTab==='permissions')await loadPlayerPermissions(false);
  }catch(e){
    $('playerCommandsNotice').className='warning';
    $('playerCommandsNotice').textContent=e.message;
    setNavStatus('playercommands','bad',e.message);
  }finally{playerCommandsLoading=false;syncPlayerCommandsDraftControls();}
}

let playerPermissionState=null;
function renderPlayerPermissions(d){
  playerPermissionState=d||{}; const ro=currentSessionRole==='ReadOnly';
  const roles=Array.isArray(d?.roles)?d.roles:[], players=Array.isArray(d?.onlinePlayers)?d.onlinePlayers:[], known=Array.isArray(d?.knownPrivileges)?d.knownPrivileges:[];
  if($('playerPermRoleRows'))$('playerPermRoleRows').innerHTML=roles.map(r=>`<tr><td><b>${esc(r.name||r.code)}</b><div class="small"><code>${esc(r.code)}</code>${r.description?` · ${esc(r.description)}`:''}</div></td><td>${Number(r.privilegeLevel||0)}</td><td><div class="small"><details><summary>${(r.privileges||[]).length} privileges</summary>${(r.privileges||[]).map(p=>`<code>${esc(p)}</code>`).join(' ')||'<span class="muted">None</span>'}</details></div></td></tr>`).join('')||'<tr><td colspan="3" class="muted">No configured roles returned.</td></tr>';
  if($('playerPermPlayerRows'))$('playerPermPlayerRows').innerHTML=players.map(p=>{
    const roleOpts=roles.map(r=>`<option value="${esc(r.code)}" ${String(r.code)===String(p.roleCode)?'selected':''}>${esc(r.name||r.code)} (${esc(r.code)})</option>`).join('');
  
    return `<tr data-player-permission-search="${esc([p.playerName,p.playerUid,p.roleCode,p.roleName].join(' ').toLowerCase())}"><td>${playerIdentityHtml(p,'playerName',true)}</td><td><select data-perm-role="${esc(p.playerUid)}" ${ro?'disabled':''}>${roleOpts}</select></td><td><div class="small">${(p.permanentPrivileges||[]).map(v=>`<code>${esc(v)}</code>`).join(' ')||'<span class="muted">None</span>'}</div></td><td><div class="small">${(p.deniedPrivileges||[]).map(v=>`<code>${esc(v)}</code>`).join(' ')||'<span class="muted">None</span>'}</div></td><td><input list="playerPermissionPrivilegeOptions" data-perm-value="${esc(p.playerUid)}" placeholder="Privilege code" aria-label="Privilege for ${esc(p.playerName||p.playerUid)}" ${ro?'disabled':''}><div class="quickActions" style="margin-top:5px"><button data-perm-act="grant" data-uid="${esc(p.playerUid)}" ${ro?'disabled':''}>Grant</button><button data-perm-act="revoke" data-uid="${esc(p.playerUid)}" ${ro?'disabled':''}>Revoke</button><button data-perm-act="deny" data-uid="${esc(p.playerUid)}" ${ro?'disabled':''}>Deny</button><button data-perm-act="undeny" data-uid="${esc(p.playerUid)}" ${ro?'disabled':''}>Un-deny</button></div></td></tr>`;
  }).join('')||'<tr><td colspan="5" class="muted">No online players.</td></tr>';
  $('playerPermissionPrivilegeOptions').innerHTML=known.map(v=>`<option value="${esc(v)}"></option>`).join('');
  filterPlayerPermissionRows();
  document.querySelectorAll('[data-perm-role]').forEach(e=>e.onchange=()=>changePlayerPermission(e.dataset.permRole,'set-role',e.value));
  document.querySelectorAll('[data-perm-act]').forEach(b=>b.onclick=()=>{const uid=b.dataset.uid, sel=document.querySelector(`[data-perm-value="${CSS.escape(uid)}"]`); changePlayerPermission(uid,b.dataset.permAct,sel?.value||'');});
}
async function loadPlayerPermissions(showLoading=true){
  if(showLoading&&$('playerPermRoleRows'))$('playerPermRoleRows').innerHTML='<tr><td colspan="3" class="muted">Loading roles and privileges…</td></tr>';
  try{renderPlayerPermissions(await api('/api/playercommands/permissions'));}catch(e){if($('playerPermRoleRows'))$('playerPermRoleRows').innerHTML=`<tr><td colspan="3" class="bad">${esc(e.message)}</td></tr>`;}
}
async function changePlayerPermission(uid,action,value){
  value=String(value||'').trim();
  if(currentSessionRole==='ReadOnly'||!uid||playerPermissionBusy)return;
  if(!value){toast('Choose a privilege code first.','error');return;}
  if(action==='set-role'&&!confirm(`Change this player's role to ${value}? This can alter many permissions at once.`)){await loadPlayerPermissions(false);return;}
  playerPermissionBusy=true;
  document.querySelectorAll('[data-perm-role],[data-perm-value],[data-perm-act]').forEach(el=>el.disabled=true);
  try{const d=await api('/api/playercommands/permissions',{method:'POST',body:JSON.stringify({playerUid:uid,action,value})});toast(d.message||'Permission updated.','success');renderPlayerPermissions(d.permissions||await api('/api/playercommands/permissions'));if(!playerCommandsDirty)await loadPlayerCommands(false);}catch(e){toast(e.message,'error');await loadPlayerPermissions(false);}
  finally{playerPermissionBusy=false;document.querySelectorAll('[data-perm-role],[data-perm-value],[data-perm-act]').forEach(el=>el.disabled=currentSessionRole==='ReadOnly');}
}

async function togglePlayerCommand(key,enabled){
  if(currentSessionRole==='ReadOnly'||!key||playerCommandsBusy||!playerCommandsState?.available)return;
  if(playerCommandsDirty){toast('Save or discard your policy edits before applying an individual command switch.','error');return;}
  playerCommandsBusy=true;syncPlayerCommandsDraftControls();
  try{
    const d=await api('/api/playercommands/command',{method:'POST',body:JSON.stringify({key,enabled})});
    renderPlayerCommands(d.playerCommands||await api('/api/playercommands'));
    toast(d.message||`Command ${enabled?'enabled':'disabled'}.`,'success');
  }catch(e){toast(e.message,'error');}
  finally{playerCommandsBusy=false;syncPlayerCommandsDraftControls();}
}

async function savePlayerCommands(){
  if(currentSessionRole==='ReadOnly'||!playerCommandsState?.available||playerCommandsBusy||playerCommandsLoading)return;
  try{
    validatePlayerCommandsDraft();
    const payload={
    spawnEnabled:!!$('playerCmdSpawnEnabled').checked,
    spawnCooldownSeconds:Math.round(Number($('playerCmdSpawnCooldown').value||0)),
    spawnWarmupSeconds:Math.round(Number($('playerCmdSpawnWarmup').value||0)),
    spawnCancelOnMove:!!$('playerCmdSpawnCancelMove').checked,
    privateMessagesEnabled:!!$('playerCmdMsgEnabled').checked,
    replyEnabled:!!$('playerCmdReplyEnabled').checked,
    respectSuiteMutes:!!$('playerCmdRespectMutes').checked,
    maxPrivateMessageLength:Math.round(Number($('playerCmdMaxLength').value||500)),
    starterKitEnabled:!!$('playerCmdStarterEnabled').checked,
    starterKitProvider:$('playerCmdStarterProvider').value||'Auto',
    warpsEnabled:!!$('playerCmdWarpsEnabled').checked,
    warpCommandEnabled:!!$('playerCmdWarpEnabled').checked,
    backEnabled:!!$('playerCmdBackEnabled').checked,
    backCooldownSeconds:Math.round(Number($('playerCmdBackCooldown').value||0)),
    backWarmupSeconds:Math.round(Number($('playerCmdBackWarmup').value||0)),
    backCancelOnMove:!!$('playerCmdBackCancelMove').checked,
    backTrackDeaths:!!$('playerCmdBackDeaths').checked,
    backTrackTeleports:!!$('playerCmdBackTeleports').checked,
    homesEnabled:!!$('playerCmdHomesEnabled').checked,
    homeGlobalLimit:Math.round(Number($('playerCmdHomeGlobalLimit').value||0)),
    homeRoleLimits:playerCommandLimitTextToMap($('playerCmdHomeRoleLimits').value),
    homePlayerLimits:playerCommandLimitTextToMap($('playerCmdHomePlayerLimits').value),
    homeCooldownSeconds:Math.round(Number($('playerCmdHomeCooldown').value||0)),
    homeWarmupSeconds:Math.round(Number($('playerCmdHomeWarmup').value||0)),
    homeCancelOnMove:!!$('playerCmdHomeCancelMove').checked,
    t2pEnabled:!!$('playerCmdT2pEnabled').checked,
    t2pCooldownSeconds:Math.round(Number($('playerCmdT2pCooldown').value||0)),
    t2mEnabled:!!$('playerCmdT2mEnabled').checked,
    t2mCooldownSeconds:Math.round(Number($('playerCmdT2mCooldown').value||0)),
    rtpEnabled:!!$('playerCmdRtpEnabled').checked,
    rtpMinRadius:Math.round(Number($('playerCmdRtpMinRadius').value||0)),
    rtpMaxRadius:Math.round(Number($('playerCmdRtpMaxRadius').value||0)),
    rtpCooldownSeconds:Math.round(Number($('playerCmdRtpCooldown').value||0)),
    rtpWarmupSeconds:Math.round(Number($('playerCmdRtpWarmup').value||0)),
    rtpCancelOnMove:!!$('playerCmdRtpCancelMove').checked,
    rtpCostEnabled:!!$('playerCmdRtpCostEnabled').checked,
    rtpCostItemCode:$('playerCmdRtpCostItem').value.trim(),
    rtpCostQuantity:Math.round(Number($('playerCmdRtpCostQty').value||1)),
    rtpMaxSearchAttempts:Math.round(Number($('playerCmdRtpAttempts').value||20)),
    rtpAvoidWaterAndLava:!!$('playerCmdRtpAvoidLiquids').checked,
    rtpCenterOnPlayerPosition:!!$('playerCmdRtpCenterPlayer').checked,
    teleportRolePolicies:collectTeleportRolePolicies(),
    rolePolicyRevision:playerCommandsState?.rolePolicyRevision
  };
    playerCommandsBusy=true;syncPlayerCommandsDraftControls();
    const d=await api('/api/playercommands/settings',{method:'POST',body:JSON.stringify(payload)});
    toast(d.message||'Player command settings saved.','success');
    renderPlayerCommands(d.playerCommands||await api('/api/playercommands'));
  }catch(e){toast(e.message,'error');}
  finally{playerCommandsBusy=false;syncPlayerCommandsDraftControls();}
}

function setAllPlayerCommands(enabled){
  if(currentSessionRole==='ReadOnly'||playerCommandsBusy||!playerCommandsState?.available)return;
  $('playerCmdSpawnEnabled').checked=enabled;
  $('playerCmdMsgEnabled').checked=enabled;
  $('playerCmdReplyEnabled').checked=enabled;
  $('playerCmdStarterEnabled').checked=enabled;
  $('playerCmdWarpsEnabled').checked=enabled;
  $('playerCmdWarpEnabled').checked=enabled;
  $('playerCmdBackEnabled').checked=enabled;
  $('playerCmdHomesEnabled').checked=enabled;
  $('playerCmdT2pEnabled').checked=enabled;
  $('playerCmdT2mEnabled').checked=enabled;
  $('playerCmdRtpEnabled').checked=enabled;
  $('playerCmdReplyEnabled').disabled=currentSessionRole==='ReadOnly'||!enabled;
  $('playerCmdWarpEnabled').disabled=currentSessionRole==='ReadOnly'||!enabled;
  updatePlayerCommandsDirty();
}

$('playerCommandsRefresh').onclick=()=>reloadPlayerCommandsDraft();
$('playerCommandsDiscard').onclick=()=>discardPlayerCommandsDraft();
if($('playerPermRefresh'))$('playerPermRefresh').onclick=()=>loadPlayerPermissions(true);
if($('playerCmdRolePolicyReset'))$('playerCmdRolePolicyReset').onclick=resetTeleportRolePolicies;
$('playerCommandsSave').onclick=savePlayerCommands;
$('playerCommandsEnableAll').onclick=()=>setAllPlayerCommands(true);
$('playerCommandsDisableAll').onclick=()=>setAllPlayerCommands(false);
$('playerCommandSearch').oninput=renderPlayerCommandDirectory;
$('playerCommandFilter').onchange=renderPlayerCommandDirectory;
$('playerCmdRoleCommand').onchange=filterPlayerCommandRoleColumns;
$('playerCmdMsgEnabled').addEventListener('change',()=>{$('playerCmdReplyEnabled').disabled=currentSessionRole==='ReadOnly'||!$('playerCmdMsgEnabled').checked;});
$('playerCmdWarpsEnabled').addEventListener('change',()=>{$('playerCmdWarpEnabled').disabled=currentSessionRole==='ReadOnly'||!$('playerCmdWarpsEnabled').checked;});

// Draft inputs are scoped to this feature; searching and switching tabs is not an edit.
playerCommandDraftFields().filter(el=>el.type==='number').forEach(el=>{el.required=true;el.step='1';});
$('playercommands').addEventListener('input',event=>{
  if(!event.target.matches(PLAYER_COMMAND_DRAFT_SELECTOR)||event.target.id==='playerCmdRoleCommand')return;
  if(event.target.matches('[data-role-policy-mode],[data-role-policy-access],[data-role-policy-cooldown]'))syncPlayerCommandRoleControls();
  updatePlayerCommandsDirty();
});
$('playercommands').addEventListener('change',event=>{
  if(event.target.matches(PLAYER_COMMAND_DRAFT_SELECTOR)&&event.target.id!=='playerCmdRoleCommand'){
    if(event.target.matches('[data-role-policy-mode],[data-role-policy-access],[data-role-policy-cooldown]'))syncPlayerCommandRoleControls();
    updatePlayerCommandsDirty();
  }
});
$('playerPermissionSearch').oninput=filterPlayerPermissionRows;
document.querySelectorAll('[data-player-command-tab]').forEach(button=>{
  button.onclick=()=>setPlayerCommandTab(button.dataset.playerCommandTab);
  button.onkeydown=event=>{
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
    event.preventDefault();const tabs=[...document.querySelectorAll('[data-player-command-tab]')],i=tabs.indexOf(button);
    const next=event.key==='Home'?0:event.key==='End'?tabs.length-1:(i+(event.key==='ArrowRight'?1:-1)+tabs.length)%tabs.length;
    setPlayerCommandTab(tabs[next].dataset.playerCommandTab);tabs[next].focus();
  };
});
document.querySelectorAll('[data-player-command-warps]').forEach(b=>b.onclick=()=>setSection('warps'));
window.addEventListener('beforeunload',event=>{if(playerCommandsDirty||playerCommandsBusy){event.preventDefault();event.returnValue='';}});
setPlayerCommandTab('commands');
syncPlayerCommandsDraftControls();

""";
}
