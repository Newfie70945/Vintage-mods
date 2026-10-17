namespace BlockLogger.WebDashboard;
internal static class WebDashboardRoles {
 public const string Html = """
<section id="roles">
<div class="pageTitle"><div><h2>Role Config</h2><div class="subtitle">Game roles, permissions, name colors and shared Suite limits in one place.</div></div></div>
<div class="toolbar"><input id="roleSearch" type="search" placeholder="Search roles…" aria-label="Search roles"><button id="rolesReload">Reload</button><button id="roleNew">Add Role</button><span id="rolesState" class="small" role="status"></span></div>
<div class="infoBox">These are Vintage Story player roles. WebPanel account access is managed under Web Dashboard. Home limits, teleport policies and chat name colors share the same settings as their original pages.</div>
<div class="roleWorkspace perfSection"><div class="card"><p id="roleListCount" class="small" role="status"></p><div id="roleList" class="roleList"></div></div><form id="roleEditor" class="card" hidden>
<div class="cardHeader"><h3 id="roleHeading">Role</h3><span id="roleDraftState" class="small" role="status" aria-live="polite"></span></div>
<div class="grid2">
<label class="field">Role code<input id="roleCode" required maxlength="64"></label><label class="field">Display name<input id="roleName" required maxlength="80"></label>
<label class="field">Description<textarea id="roleDescription" maxlength="500"></textarea></label>
<label class="field">Rank / privilege level<input id="roleRank" type="number" min="0" max="100000" required></label>
<label class="field">Game role color<input id="roleColor" type="color"></label>
<label class="field">Chat / roster color override (role code)<input id="roleNameColor" placeholder="Empty = inherit chat color" pattern="#[0-9a-fA-F]{6}"></label>
<label class="field">Default game mode<select id="roleGameMode"></select></label>
<label class="field">Home limit<input id="roleHomeLimit" type="number" min="0" max="100" placeholder="Inherit global limit"></label>
<label class="field">Claim block allowance<input id="roleClaimAllowance" type="number" min="0" max="2147483647" required></label><label class="field">Maximum claim areas<input id="roleClaimAreas" type="number" min="0" max="10000" required></label>
</div>
<div class="row"><label class="field">Minimum claim X<input id="roleClaimX" type="number" min="1" max="10000" required></label><label class="field">Y<input id="roleClaimY" type="number" min="1" max="10000" required></label><label class="field">Z<input id="roleClaimZ" type="number" min="1" max="10000" required></label></div>
<label class="toggleLine"><input id="roleAutoGrant" type="checkbox"> <span>Automatically grant every registered game privilege</span></label>
<label class="toggleLine"><input id="roleUseColors" type="checkbox"> <span>Enable role name colors globally</span></label>
<h3>Permissions</h3><p class="small">Persistent privilege codes, including mod privileges. Runtime grants remain controlled by their originating mods.</p><input id="rolePrivilegeSearch" type="search" placeholder="Find a privilege…" aria-label="Find a privilege"><div id="rolePrivilegeList" class="rolePrivileges"></div><label class="field">Additional privilege codes (one per line)<textarea id="roleExtraPrivileges" rows="3"></textarea></label><div id="roleRuntimePrivileges" class="small"></div>
<h3>Teleport access &amp; cooldowns</h3><p id="rolePolicyNote" class="small"></p><div id="rolePolicies" class="rolePolicies"></div>
<div class="toolbar"><button id="rolePreview" type="button">Preview Changes</button><button id="roleSave" class="primary" type="submit">Save Role</button><button id="roleDiscard" type="button">Discard Draft</button><div class="spacer"></div><button id="roleDelete" class="danger" type="button">Remove Role</button></div>
<div id="rolePreviewResult" class="compactNote" role="status" hidden></div>
</form></div>
<div class="card perfSection"><div class="cardHeader"><h3>Player Role Assignments</h3></div><div class="toolbar"><input id="rolePlayerSearch" type="search" placeholder="Player name, UID or role…" aria-label="Find players"><button id="rolePlayerFind">Find Players</button><span id="rolePlayerCount" class="small"></span></div><div class="toolbar"><input id="rolePlayerTargetSearch" type="search" placeholder="Find target role…" aria-label="Find target role"><select id="rolePlayerTarget" aria-label="Target role for assignment"></select></div><p class="small">Choose a target role above, then assign it to a player. Search saved players, including offline accounts. Direct privilege grants and denials are preserved. Discord role sync may reassign linked players later; review its mappings before removing a role.</p><div class="toolbar"><button id="roleBatchPreview">Preview Selected</button><button id="roleBatchApply" class="primary" disabled>Apply Previewed Assignments</button><span id="roleBatchState" class="small" role="status"></span></div><div id="roleEffectiveResult" class="compactNote" hidden></div><div id="rolePlayers" class="rolePlayers"></div></div>
</section>
""";
 public const string Script = """
let rolesData=null,roleSelected='',roleDrafts=new Map(),roleBusy=false,rolePlayersData=[];
let rolePlayersLoading=false,rolePlayersReload=false;
const roleAssignmentsPending=new Set();
const ROLE_COMMANDS=['spawn','home','back','warp','t2p','t2m','rtp'];
const roleFields={code:'roleCode',name:'roleName',description:'roleDescription',privilegeLevel:'roleRank',colorHex:'roleColor',nameColorHex:'roleNameColor',defaultGameMode:'roleGameMode',homeLimit:'roleHomeLimit',landClaimAllowance:'roleClaimAllowance',landClaimMaxAreas:'roleClaimAreas',claimMinX:'roleClaimX',claimMinY:'roleClaimY',claimMinZ:'roleClaimZ'};
function roleCanWrite(){return rolesData?.available===true&&currentSessionRole!=='ReadOnly'&&!roleBusy;}
function roleRead(){
 const row={};for(const [key,id] of Object.entries(roleFields)){const el=$(id);row[key]=el.type==='number'?(el.value===''?null:Number(el.value)):el.value;}
 row.autoGrant=$('roleAutoGrant').checked;row.privileges=[...document.querySelectorAll('[data-role-privilege]:checked')].map(e=>e.value).concat($('roleExtraPrivileges').value.split(/[\s,]+/).filter(Boolean));row.policies={};
 for(const cmd of ROLE_COMMANDS){const mode=$(`rolePolicy_${cmd}`).value;row.policies[cmd]=mode==='inherit'?null:{allowed:mode==='allow',cooldownSeconds:Number($(`roleCooldown_${cmd}`).value)};}
 return {row,useColors:$('roleUseColors').checked,revision:roleDrafts.get(roleSelected)?.revision||rolesData.revision};
}
function roleDefinitionSignature(draft){
 const row=draft.row,numberKeys=new Set(['privilegeLevel','homeLimit','landClaimAllowance','landClaimMaxAreas','claimMinX','claimMinY','claimMinZ']);
 return JSON.stringify([
  Object.keys(roleFields).map(key=>[key,numberKeys.has(key)?(row[key]===''||row[key]==null?null:Number(row[key])):key==='colorHex'||key==='nameColorHex'?String(row[key]??'').toUpperCase():String(row[key]??'')]),
  !!row.autoGrant,[...new Set(row.privileges||[])].sort(),
  ROLE_COMMANDS.map(cmd=>{const p=row.policies?.[cmd];return[cmd,p?{allowed:p.allowed!==false,cooldownSeconds:Number(p.cooldownSeconds??-1)}:null];}),!!draft.useColors
 ]);
}
function roleCapture(){
 if(!roleSelected||$('roleEditor').hidden||!roleCanWrite())return;
 const draft=roleRead(),saved=rolesData.roles.find(r=>r.code===roleSelected),hadDraft=roleDrafts.has(roleSelected);
 if(saved&&roleDefinitionSignature(draft)===roleDefinitionSignature({row:saved,useColors:rolesData.useRoleNameColors}))roleDrafts.delete(roleSelected);
 else roleDrafts.set(roleSelected,draft);
 $('roleDraftState').textContent=roleDrafts.has(roleSelected)?'Unsaved draft':'Saved definition';
 if(hadDraft!==roleDrafts.has(roleSelected))renderRoleList();
 roleSync();
}
function roleSync(){document.querySelectorAll('[data-role-select]').forEach(e=>e.disabled=roleBusy);$('roleSearch').disabled=roleBusy;const blocked=!roleCanWrite();document.querySelectorAll('#roleEditor input,#roleEditor textarea,#roleEditor select,#roleEditor button').forEach(e=>e.disabled=blocked);$('roleCode').disabled=blocked||roleSelected!=='__new';$('roleDelete').disabled=blocked||roleSelected==='__new'||!!rolesData?.roles.find(r=>r.code===roleSelected)?.isDefault;$('roleSave').disabled=blocked||!roleDrafts.has(roleSelected);$('roleDiscard').disabled=roleBusy||!roleDrafts.has(roleSelected);$('roleNew').disabled=blocked;$('rolesReload').disabled=roleBusy;for(const cmd of ROLE_COMMANDS){$(`roleCooldown_${cmd}`).disabled=blocked||!rolesData.policiesAvailable||$(`rolePolicy_${cmd}`).value==='inherit';$(`rolePolicy_${cmd}`).disabled=blocked||!rolesData.policiesAvailable;}$('roleHomeLimit').disabled=blocked||!rolesData.policiesAvailable;$('roleNameColor').disabled=blocked||!rolesData.colorsAvailable;$('roleUseColors').disabled=blocked||!rolesData.colorsAvailable;}
function renderRoleList(){const q=$('roleSearch').value.toLowerCase().trim();const all=rolesData?.roles||[],rows=all.filter(r=>[r.code,r.name,r.description].join(' ').toLowerCase().includes(q));$('roleListCount').textContent=`${rows.length} of ${all.length} roles${roleDrafts.size?` · ${roleDrafts.size} unsaved draft${roleDrafts.size===1?'':'s'}`:''}`;$('roleList').innerHTML=(roleDrafts.has('__new')?`<button type="button" data-role-select="__new" class="${roleSelected==='__new'?'primary':''}" ${roleBusy?'disabled':''}><b>New Role</b><span>Unsaved draft</span></button>`:'')+rows.map(r=>`<button type="button" data-role-select="${esc(r.code)}" class="${roleSelected===r.code?'primary':''}" ${roleBusy?'disabled':''}><b>${esc(r.name)}</b><span>${esc(r.code)} · ${r.assignedPlayers} players${r.isDefault?' · Default':''}${roleDrafts.has(r.code)?' · Unsaved draft':''}</span></button>`).join('');if(!$('roleList').innerHTML)$('roleList').innerHTML='<p class="small">No matching roles.</p>';document.querySelectorAll('[data-role-select]').forEach(b=>b.onclick=()=>{if(roleBusy)return;roleSelected=b.dataset.roleSelect;renderRoleEditor();renderRoleList();});}
function renderRoleEditor(){
 $('rolePreviewResult').hidden=true;
 const saved=rolesData?.roles.find(r=>r.code===roleSelected);const draft=roleDrafts.get(roleSelected);const row=draft?.row||saved;
 $('roleEditor').hidden=!row;if(!row)return;
 $('roleHeading').textContent=roleSelected==='__new'?'New Role':row.name;$('roleDraftState').textContent=draft?'Unsaved draft':'Saved definition';
 for(const [key,id] of Object.entries(roleFields))$(id).value=row[key]??'';
 $('roleGameMode').innerHTML=rolesData.gameModes.map(mode=>`<option>${esc(mode)}</option>`).join('');$('roleGameMode').value=row.defaultGameMode;
 $('roleAutoGrant').checked=!!row.autoGrant;$('roleUseColors').checked=draft?draft.useColors:rolesData.useRoleNameColors;
 const known=rolesData.knownPrivileges||[];$('rolePrivilegeList').innerHTML=known.map(code=>`<label class="toggleLine" data-role-privilege-label="${esc(code)}"><input type="checkbox" data-role-privilege value="${esc(code)}" ${row.privileges?.includes(code)?'checked':''}> <span>${esc(code)}</span></label>`).join('');$('roleExtraPrivileges').value=(row.privileges||[]).filter(code=>!known.includes(code)).join('\n');$('roleRuntimePrivileges').textContent=`Runtime grants: ${(saved?.runtimePrivileges||[]).join(', ')||'none'}`;
 $('rolePolicyNote').textContent=`Home inheritance: global limit ${rolesData.globalHomeLimit}. Inherited teleport policies use wildcard overrides where present, then the command default. −1 keeps the inherited cooldown. Warmups remain in Player Commands.`;
 $('rolePolicies').innerHTML=ROLE_COMMANDS.map(cmd=>{const p=row.policies?.[cmd],wild=rolesData.wildcardPolicies?.[cmd],fallback=wild?.cooldownSeconds>=0?wild.cooldownSeconds:rolesData.defaultCooldowns?.[cmd];const access=wild?wild.allowed:(cmd==='t2p'||cmd==='t2m')?!!(row.autoGrant||row.privileges?.some(c=>c==='controlserver'||c==='root'||c==='*')):true;return `<div><b>/${cmd}</b><label class="field">Access<select id="rolePolicy_${cmd}"><option value="inherit">Inherit (${access?'allowed':'denied'})</option><option value="allow">Allow</option><option value="deny">Deny</option></select></label><label class="field">Cooldown (seconds)<input id="roleCooldown_${cmd}" type="number" min="-1" max="604800" value="${p?.cooldownSeconds??-1}" required></label><small>Inherited: ${fallback??0}s</small></div>`;}).join('');for(const cmd of ROLE_COMMANDS)$(`rolePolicy_${cmd}`).value=row.policies?.[cmd]?(row.policies[cmd].allowed?'allow':'deny'):'inherit';
 roleSync();filterRolePrivileges();
}
function filterRolePrivileges(){const q=$('rolePrivilegeSearch').value.toLowerCase().trim();document.querySelectorAll('[data-role-privilege-label]').forEach(e=>e.hidden=!e.dataset.rolePrivilegeLabel.toLowerCase().includes(q));}
async function loadRoles(force=false){if(roleBusy)return;if(roleDrafts.size&&!force){$('rolesState').textContent='Drafts kept; reload to fetch newer definitions.';return;}if(force&&roleDrafts.size&&!confirm('Discard role drafts and reload?'))return;roleBusy=true;$('rolesState').textContent='Loading…';try{rolesData=await api('/api/roles');roleDrafts.clear();if(!rolesData.roles.some(r=>r.code===roleSelected))roleSelected=rolesData.roles[0]?.code||'';$('rolesState').textContent=rolesData.available?`${rolesData.roles.length} roles`:'Role definitions unavailable';}catch(e){toast(e.message,'error');$('rolesState').textContent=e.message;}finally{roleBusy=false;renderRoleList();renderRoleEditor();renderRoleTargets();}}
async function saveRole(action){if(!roleCanWrite())return;if(action!=='delete'&&!$('roleEditor').reportValidity())return;if(action==='delete'&&!confirm(`Remove ${roleSelected}? Roles assigned to players cannot be removed.`))return;roleCapture();if(action==='save'&&!roleDrafts.has(roleSelected))return;const draft=roleDrafts.get(roleSelected)||roleRead();roleBusy=true;roleSync();try{const d=await api('/api/roles',{method:'POST',body:JSON.stringify({action:action==='save'&&roleSelected==='__new'?'create':action,revision:draft.revision,role:draft.row,useRoleNameColors:draft.useColors})});if(!d.success)throw new Error(d.message);roleDrafts.delete(roleSelected);roleSelected=action==='delete'?'':draft.row.code.toLowerCase();rolesData=d.snapshot;if(!roleSelected)roleSelected=rolesData.roles[0]?.code||'';$('rolesState').textContent=`${rolesData.roles.length} roles`;toast(d.message,'success');}catch(e){toast(e.message,'error');$('rolesState').textContent=e.message;}finally{roleBusy=false;renderRoleList();renderRoleEditor();renderRoleTargets();renderRolePlayers();}}
function renderRoleTargets(){const el=$('rolePlayerTarget'),selected=el.value,q=$('rolePlayerTargetSearch').value.toLowerCase().trim();const rows=(rolesData?.roles||[]).filter(r=>[r.code,r.name].join(' ').toLowerCase().includes(q));el.innerHTML=rows.map(r=>`<option value="${esc(r.code)}">${esc(r.name)} (${esc(r.code)})</option>`).join('');if(rows.some(r=>r.code===selected))el.value=selected;el.disabled=!roleCanWrite();if(selected!==el.value)invalidateRoleBatch();}
function renderRolePlayers(){
 $('rolePlayers').innerHTML=rolePlayersData.map((p,i)=>`<div><input type="checkbox" data-role-batch-uid="${esc(p.playerUid)}" aria-label="Select ${esc(p.playerName||p.playerUid)}" ${currentSessionRole==='ReadOnly'?'disabled':''}><span><b>${esc(p.playerName||p.playerUid)}</b><small>${p.online?'Online':'Offline'} · ${esc(p.roleCode)}</small></span><button data-role-effective="${i}">Effective Policy</button><button data-role-assign="${i}" ${roleCanWrite()&&!roleAssignmentsPending.has(p.playerUid)?'':'disabled'}>${roleAssignmentsPending.has(p.playerUid)?'Assigning…':'Assign'}</button></div>`).join('')||'<p class="small">No matching saved players.</p>';
 bindRolePolish();
 document.querySelectorAll('[data-role-assign]').forEach(b=>b.onclick=async()=>{
  const p=rolePlayersData[Number(b.dataset.roleAssign)],roleCode=$('rolePlayerTarget').value;
  if(!roleCanWrite()||!roleCode||!p||roleAssignmentsPending.has(p.playerUid))return;
  roleAssignmentsPending.add(p.playerUid);renderRolePlayers();
  try{const d=await api('/api/roles/player',{method:'POST',body:JSON.stringify({playerUid:p.playerUid,roleCode,expectedRoleCode:p.roleCode})});if(!d.success)throw new Error(d.message);toast(d.message,'success');await findRolePlayers();}
  catch(e){toast(e.message,'error');}
  finally{roleAssignmentsPending.delete(p.playerUid);renderRolePlayers();}
 });
}
async function findRolePlayers(){
 if(rolePlayersLoading){rolePlayersReload=true;return;}
 rolePlayersLoading=true;rolePlayersReload=false;$('rolePlayerCount').textContent='Searching players…';
 const search=$('rolePlayerSearch').value.trim();
 try{
  renderRoleTargets();const d=await api('/api/roles/players?search='+encodeURIComponent(search));
  if(search!==$('rolePlayerSearch').value.trim()){rolePlayersReload=true;return;}
  rolePlayersData=d.players||[];$('rolePlayerCount').textContent=`Showing ${rolePlayersData.length} of ${d.total}`;renderRolePlayers();
 }catch(e){$('rolePlayerCount').textContent='Search failed';toast(e.message,'error');}
 finally{rolePlayersLoading=false;if(rolePlayersReload){rolePlayersReload=false;await findRolePlayers();}}
}
$('roleSearch').oninput=renderRoleList;$('rolesReload').onclick=()=>loadRoles(true);$('roleNew').onclick=()=>{if(!roleCanWrite())return;roleSelected='__new';roleDrafts.set(roleSelected,{revision:rolesData.revision,useColors:rolesData.useRoleNameColors,row:{code:'',name:'',description:'',privilegeLevel:0,colorHex:'#FFFFFF',nameColorHex:'',defaultGameMode:'Survival',homeLimit:null,landClaimAllowance:0,landClaimMaxAreas:0,claimMinX:5,claimMinY:5,claimMinZ:5,privileges:[],policies:{}}});renderRoleEditor();renderRoleList();};
$('roleEditor').onsubmit=e=>{e.preventDefault();saveRole('save');};$('roleDelete').onclick=()=>saveRole('delete');$('roleDiscard').onclick=()=>{if(roleBusy||!confirm('Discard this role draft?'))return;roleDrafts.delete(roleSelected);if(roleSelected==='__new')roleSelected=rolesData.roles[0]?.code||'';renderRoleEditor();renderRoleList();};
$('roleEditor').addEventListener('input',e=>{if(e.target.id==='rolePrivilegeSearch')return;roleCapture();});$('roleEditor').addEventListener('change',e=>{if(e.target.id==='rolePrivilegeSearch')return;roleCapture();roleSync();});$('rolePrivilegeSearch').oninput=filterRolePrivileges;$('rolePlayerFind').onclick=findRolePlayers;$('rolePlayerTargetSearch').oninput=renderRoleTargets;$('rolePlayerSearch').onkeydown=e=>{if(e.key==='Enter')findRolePlayers();};
window.addEventListener('beforeunload',e=>{if(roleDrafts.size){e.preventDefault();e.returnValue='';}});

""";
}
