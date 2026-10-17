namespace BlockLogger.WebDashboard;

internal static class WebDashboardPatchGeneratorScript
{
    public const string Script = """
let pgLoaded=false,pgInspection=null,pgResult=null,pgPreviewRequest='',pgId='',pgSearchTimer=null,pgCatalogSequence=0,pgAssetSequence=0,pgBusy=false,pgScalarKind='';
function pgInvalidate(){pgResult=null;pgPreviewRequest='';$('pgPreviewPanel').hidden=true;pgAccess();}
function pgAccess(){
  const ro=currentSessionRole==='ReadOnly';
  $('pgPreview').disabled=ro||pgBusy||!pgInspection;
  $('pgSaveDraft').disabled=!currentSessionOwner||pgBusy||!pgResult;
  $('pgApply').disabled=!currentSessionOwner||pgBusy||!pgResult;
  $('pgDownload').disabled=!pgResult;
}
async function pgEnsureLoaded(){pgAccess();if(!pgLoaded){await pgSearchAssets();pgLoaded=true;}}
async function pgSearchAssets(){
  const sequence=++pgCatalogSequence,kind=$('pgKind').value,search=$('pgSearch').value.trim();
  try{
    const d=await api('/api/content-patches/assets?kind='+encodeURIComponent(kind)+'&search='+encodeURIComponent(search));
    if(sequence!==pgCatalogSequence)return;
    const previous=$('pgAsset').value,assets=d.assets||[];
    $('pgAsset').innerHTML='<option value="">Choose an asset…</option>'+assets.map(x=>`<option value="${esc(x.file)}">${esc(x.file)}</option>`).join('');
    if(assets.some(x=>x.file===previous))$('pgAsset').value=previous;
    else if(previous){pgInspection=null;pgInvalidate();}
    $('pgCatalogState').textContent=`${assets.length} shown / ${Number(d.totalMatches||0)} matching assets${Number(d.totalMatches||0)>assets.length?' · narrow your search':''}`;
  }catch(e){if(sequence===pgCatalogSequence)$('pgCatalogState').textContent=e.message;}
}
async function pgSelectAsset(){
  const sequence=++pgAssetSequence,file=$('pgAsset').value;pgInspection=null;pgId='';pgInvalidate();
  if(!file)return;
  $('pgCatalogState').textContent='Reading asset…';
  try{
    const d=await api('/api/content-patches/asset?file='+encodeURIComponent(file));
    if(sequence!==pgAssetSequence||file!==$('pgAsset').value)return;
    pgInspection=d;
    $('pgRoot').innerHTML=(d.roots||[]).map(x=>`<option value="${esc(x.path)}">${esc(x.label)}${x.path?' · '+esc(x.path):''}</option>`).join('');
    const recipe=d.kind==='recipe';
    $('pgPreset').innerHTML=(recipe?[
      ['recipe-output-quantity','Recipe output quantity'],['recipe-output-code','Recipe output item / block'],
      ['recipe-ingredient-quantity','Ingredient quantity'],['recipe-ingredient-code','Ingredient item / block'],['recipe-enabled','Enable / disable recipe']
    ]:d.kind==='item'?[['item-stacksize','Maximum stack size']]:[]).concat([['property','Edit an existing property']]).map(([v,t])=>`<option value="${v}">${t}</option>`).join('');
    $('pgSide').value=recipe?'server':'';
    $('pgCatalogState').textContent=d.fieldsTruncated?'Loaded · first 600 fields shown; advanced editor available.':'Asset loaded. Choose a change below.';
    pgRootChanged();
  }catch(e){if(sequence===pgAssetSequence){$('pgCatalogState').textContent=e.message;toast(e.message,'error');}}
}
function pgRootChanged(){
  const root=(pgInspection?.roots||[]).find(x=>x.path===$('pgRoot').value);
  $('pgIngredient').innerHTML=(root?.ingredients||[]).map(x=>`<option value="${esc(x.key)}">${esc(x.key)} · ${esc(x.code)}</option>`).join('');
  const prefix=$('pgRoot').value;
  $('pgField').innerHTML=(pgInspection?.fields||[]).filter(x=>x.path.startsWith(prefix+'/')).map(x=>`<option value="${esc(x.path)}">${esc(x.path)} · ${esc(x.summary)}</option>`).join('');
  pgChangeType();
}
function pgChangeType(){
  const preset=$('pgPreset').value,property=preset==='property';
  for(const [id,visible] of [['pgFieldWrap',property],['pgOperationWrap',property],['pgValueWrap',property&&$('pgOperation').value!=='remove'],['pgIngredientWrap',preset.startsWith('recipe-ingredient')],['pgQuantityWrap',preset.endsWith('quantity')||preset==='item-stacksize'],['pgCodeWrap',preset.endsWith('code')],['pgRecipeEnabledWrap',preset==='recipe-enabled']])$(id).hidden=!visible;
  if(property)pgFillField();
  else{
    const root=$('pgRoot').value,ingredient=$('pgIngredient').value.replaceAll('~','~0').replaceAll('/','~1');
    const suffix=preset==='item-stacksize'?'/maxStackSize':preset==='recipe-enabled'?'/enabled':preset.startsWith('recipe-ingredient')?'/ingredients/'+ingredient+'/'+(preset.endsWith('quantity')?'quantity':'code'):'/output/'+(preset.endsWith('quantity')?'quantity':'code');
    const field=(pgInspection?.fields||[]).find(x=>x.path===root+suffix);
    if(field?.valueJson){try{const value=JSON.parse(field.valueJson);if(preset.endsWith('quantity')||preset==='item-stacksize')$('pgQuantity').value=Number(value)||1;else if(preset.endsWith('code'))$('pgCode').value=String(value);else $('pgRecipeEnabled').value=String(value);}catch{}}
  }
  if(property)pgValueControls();else{$('pgScalarWrap').hidden=true;}
  pgInvalidate();
}
function pgFillField(){
  const field=(pgInspection?.fields||[]).find(x=>x.path===$('pgField').value);$('pgValue').value=field?.valueJson||'null';
  pgScalarKind=['Integer','Float'].includes(field?.type)?'number':field?.type==='Boolean'?'boolean':field?.type==='String'?'string':'';
  if(pgScalarKind){try{const value=JSON.parse(field.valueJson);if(pgScalarKind==='number')$('pgScalarNumber').value=value;else if(pgScalarKind==='boolean')$('pgScalarBoolean').value=String(value);else $('pgScalarText').value=value;}catch{pgScalarKind='';}}
  pgValueControls();pgInvalidate();
}
function pgValueControls(){
  const editing=$('pgPreset').value==='property'&&$('pgOperation').value!=='remove';
  $('pgValueWrap').hidden=!editing||!!pgScalarKind;$('pgScalarWrap').hidden=!editing||!pgScalarKind;
  $('pgScalarNumber').hidden=pgScalarKind!=='number';$('pgScalarBoolean').hidden=pgScalarKind!=='boolean';$('pgScalarText').hidden=pgScalarKind!=='string';
}
function pgValueJson(){if($('pgPreset').value!=='property'||!pgScalarKind)return $('pgValue').value;if(pgScalarKind==='number'){const raw=$('pgScalarNumber').value.trim();return raw&&Number.isFinite(Number(raw))?JSON.stringify(Number(raw)):'';}return JSON.stringify(pgScalarKind==='boolean'?$('pgScalarBoolean').value==='true':$('pgScalarText').value);}
function pgRequest(){return {guided:true,id:pgId,name:$('pgName').value.trim(),file:$('pgAsset').value,rootPath:$('pgRoot').value,preset:$('pgPreset').value,path:$('pgField').value,operation:$('pgOperation').value,valueJson:pgValueJson(),ingredientKey:$('pgIngredient').value,quantity:Number($('pgQuantity').value),code:$('pgCode').value.trim(),enabled:$('pgRecipeEnabled').value==='true',side:$('pgSide').value,notes:$('pgNotes').value.trim()};}
async function pgGenerate(){
  if(currentSessionRole==='ReadOnly'||pgBusy)return;
  const request=pgRequest(),key=JSON.stringify(request);pgBusy=true;pgAccess();
  try{
    const d=await api('/api/content-patches/generate',{method:'POST',body:key});
    if(key!==JSON.stringify(pgRequest()))return;
    if(!d.success||!d.generated||!d.preview)throw Error(d.message||'Could not generate this change.');
    pgResult=d;pgPreviewRequest=key;$('pgBefore').textContent=d.preview.beforeJson;$('pgAfter').textContent=d.preview.afterJson;$('pgJson').textContent=d.preview.patchJson;
    $('pgTarget').textContent=d.generated.file+' → '+d.generated.path;$('pgPreviewNote').textContent=d.preview.note;
    $('pgPreviewPanel').hidden=false;toast('Patch generated. Review the values before staging.','success');
  }catch(e){toast(e.message,'error');}finally{pgBusy=false;pgAccess();}
}
async function pgSave(activate){
  if(!currentSessionOwner||!pgResult||pgBusy)return;
  if(JSON.stringify(pgRequest())!==pgPreviewRequest){pgInvalidate();toast('Generate a new preview after changing the form.','error');return;}
  const request={...JSON.parse(pgPreviewRequest),activate};pgBusy=true;pgAccess();
  try{
    const d=await api('/api/content-patches/build',{method:'POST',body:JSON.stringify(request)});
    if(!d.success)throw Error(d.message||'Save failed.');
    contentPatchesData=d.snapshot||await api('/api/content-patches');renderContentPatches();
    const saved=(contentPatchesData.patches||[]).find(x=>x.id===request.id)||(contentPatchesData.patches||[]).findLast(x=>x.file===pgResult.generated.file&&x.path===pgResult.generated.path);
    pgId=saved?.id||'';pgPreviewRequest=JSON.stringify(pgRequest());
    toast(activate?'Patch staged. Restart the server to apply it.':'Disabled draft saved.','success');
  }catch(e){toast(e.message,'error');}finally{pgBusy=false;pgAccess();}
}
function pgDownload(){if(!pgResult)return;const url=URL.createObjectURL(new Blob([pgResult.preview.patchJson],{type:'application/json'})),a=document.createElement('a');a.href=url;a.download='balance-patch.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function pgReset(){pgId='';$('pgName').value='';$('pgNotes').value='';pgInvalidate();}
$('pgSearch').addEventListener('input',()=>{clearTimeout(pgSearchTimer);pgSearchTimer=setTimeout(pgSearchAssets,250);});
$('pgKind').onchange=()=>{pgInspection=null;pgId='';pgInvalidate();pgSearchAssets();};
$('pgAsset').onchange=pgSelectAsset;$('pgRoot').onchange=pgRootChanged;$('pgPreset').onchange=pgChangeType;$('pgIngredient').onchange=pgChangeType;$('pgField').onchange=pgFillField;$('pgOperation').onchange=()=>{pgInvalidate();pgValueControls();};
for(const id of ['pgName','pgQuantity','pgCode','pgValue','pgRecipeEnabled','pgSide','pgNotes','pgScalarNumber','pgScalarText','pgScalarBoolean'])$(id).addEventListener('input',pgInvalidate);
$('pgPreview').onclick=pgGenerate;$('pgSaveDraft').onclick=()=>pgSave(false);$('pgApply').onclick=()=>pgSave(true);$('pgDownload').onclick=pgDownload;$('pgReset').onclick=pgReset;
$('pgExport').onclick=()=>window.open('/api/content-patches/export','_blank','noopener');
""";
}
