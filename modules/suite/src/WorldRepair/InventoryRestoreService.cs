using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed record InventoryRestoreRequest(int X,int Y,int Z,int Dimension,string Mode,long SnapshotId,bool CreateBackup=true,bool ManualBackupAcknowledged=false,bool RiskAcknowledged=false);
    public sealed class InventoryRestorePlan
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N");
        public string State {get;set;}="running";
        public string World {get;set;}="";
        public string Registry {get;set;}="";
        public InventoryRestoreRequest Request {get;set;}=new(-1,-1,-1,0,"retained",0);
        public DateTime CreatedUtc {get;set;}=DateTime.UtcNow;
        public string ChunkKey {get;set;}="";
        public int Source {get;set;}
        public int Target {get;set;}
        public string TargetCode {get;set;}="";
        public string EntityClass {get;set;}="";
        public string EntityHash {get;set;}="";
        public string SnapshotHash {get;set;}="";
        public DateTime? SnapshotUtc {get;set;}
        public long Items {get;set;}
        public int OccupiedSlots {get;set;}
        public string Fidelity {get;set;}="";
        public string Backup {get;set;}="";
        public string Message {get;set;}="";
        public bool Changed {get;set;}
        public List<BlockHistoryAdvisor.SnapshotSlot> DesiredSlots {get;set;}=[];
    }
    private sealed record RestoreTarget(int Id,string Class,bool Supported,Dictionary<string,int[]>? UsableSlots);
    private sealed record RestoreItem(int Id,EnumItemClass Kind,int Maximum);
    private sealed class RestoreCatalog
    {
        public Dictionary<string,RestoreTarget> Blocks {get;}=new(StringComparer.Ordinal);
        public Dictionary<string,RestoreItem> Items {get;}=new(StringComparer.Ordinal);
        public HashSet<(string,int)> KnownIds {get;}=[];
    }
    private InventoryRestorePlan? restorePreview,restorePending,restoreLast;
    private string RestorePendingFile=>Path.Combine(directory,"inventory-pending.json");
    private string RestoreHistoryFile=>Path.Combine(directory,"inventory-last.json");
    private void LoadInventoryRestore()
    {
        try{restorePending=Read<InventoryRestorePlan>(RestorePendingFile);restoreLast=Read<InventoryRestorePlan>(RestoreHistoryFile);}
        catch(Exception e){restoreLast=new(){State="failed",Message="Inventory restore state is unreadable: "+e.Message};api.Logger.Error("[InventoryRestore] {0}",restoreLast.Message);}
    }
    private static string Canonical(string code)=>code.Contains(':')?code:"game:"+code;
    private string RestoreRegistry()
    {
        byte[] bytes=api.WorldManager.SaveGame.GetData("ItemIDs")??throw new InvalidOperationException("Item ID mapping is not ready.");
        using var stream=new MemoryStream(bytes);var items=Serializer.Deserialize<Dictionary<int,string>>(stream);
        return Fingerprint(Mapping())+":"+Fingerprint(items);
    }
    private static bool SupportedContainer(string name)
    {
        try{
            var type=ServerMain.ClassRegistry.GetBlockEntityType(name);
            if(type.Assembly!=typeof(BlockEntityBookshelf).Assembly||type.FullName is not ("Vintagestory.GameContent.BlockEntityBookshelf" or "Vintagestory.GameContent.BlockEntityShelf" or "Vintagestory.GameContent.BlockEntityGenericContainer" or "Vintagestory.GameContent.BlockEntityGenericTypedContainer"))return false;
            foreach(string method in new[]{"FromTreeAttributes","Initialize"})if(Harmony.GetPatchInfo(AccessTools.Method(type,method))?.Owners.Count>0)return false;
            return true;
        }catch{return false;}
    }
    private RestoreCatalog CaptureRestoreCatalog()
    {
        var result=new RestoreCatalog();var supported=new Dictionary<string,bool>(StringComparer.Ordinal);
        foreach(var block in api.World.Blocks.Where(b=>b?.Code!=null&&!b.IsMissing)){
            string code=block.Code.ToString(),entity=block.EntityClass??"";
            if(!supported.ContainsKey(entity))supported[entity]=entity.Length>0&&SupportedContainer(entity);
            result.Blocks[code]=new(block.BlockId,entity,code.StartsWith("game:",StringComparison.Ordinal)&&supported[entity]&&!block.ForFluidsLayer,entity=="Bookshelf"?block.Attributes?["usableSlots"].AsObject<Dictionary<string,int[]>>():null);
            if(block.BlockId>0){result.Items[code]=new(block.BlockId,EnumItemClass.Block,block.MaxStackSize);result.KnownIds.Add(("Block",block.BlockId));}
        }
        foreach(var item in api.World.Items.Where(i=>i?.Code!=null&&!i.IsMissing)){
            string code=item.Code.ToString();if(result.Items.ContainsKey(code))result.Items.Remove(code);else result.Items[code]=new(item.ItemId,EnumItemClass.Item,item.MaxStackSize);
            result.KnownIds.Add(("Item",item.ItemId));
        }
        return result;
    }
    private static void ValidateRestoreRequest(InventoryRestoreRequest request)
    {
        if(request.X<0||request.Z<0||request.X/32>4194303||request.Z/32>4194303||request.Y<0||request.Y/32>511||request.Dimension<0||request.Dimension>1023)throw new ArgumentException("Use absolute saved coordinates and a valid dimension.");
        if(request.Mode is not ("retained" or "snapshot"))throw new ArgumentException("Choose retained or snapshot restoration.");
        if(!request.CreateBackup&&!request.ManualBackupAcknowledged)throw new InvalidOperationException("Make a manual world copy and acknowledge it before disabling automatic backup.");
        if(!request.RiskAcknowledged)throw new InvalidOperationException("Acknowledge the restoration risks and, for snapshots, missing attributes and possible duplication from unlogged transfers.");
    }
    public object StartInventoryRestorePreview(InventoryRestoreRequest request)
    {
        Guard();ValidateRestoreRequest(request);lock(gate){
            if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for or cancel the current scan/inspection.");
            if(pending!=null||restorePending!=null)throw new InvalidOperationException("Cancel the staged job before creating another restore.");
            var p=new InventoryRestorePlan{World=api.World.SavegameIdentifier,Registry=RestoreRegistry(),Request=request,ChunkKey=unchecked((long)ChunkPos.ToChunkIndex(request.X/32,request.Y/32,request.Z/32,request.Dimension)).ToString(System.Globalization.CultureInfo.InvariantCulture)};
            var catalog=CaptureRestoreCatalog();string worldPath=Database().DatabaseFilename,history=historyDatabasePath??"",receipt=RestoreReceiptPath(p);var server=(ServerMain)api.World;
            restorePreview=new(){Id=p.Id,World=p.World,Registry=p.Registry,Request=request,ChunkKey=p.ChunkKey};cancel?.Dispose();cancel=new();var token=cancel.Token;
            worker=Task.Run(()=>{var pool=new ChunkDataPool(32,server){CacheSize=4};try{token.ThrowIfCancellationRequested();PrepareRestore(p,ReadRestoreChunk(worldPath,p.ChunkKey),history,receipt,catalog,new BlockIdChunkCodec(pool),false);token.ThrowIfCancellationRequested();p.State="preview";}
                catch(OperationCanceledException){p.State="cancelled";p.Message="Restore preview cancelled.";}catch(Exception e){p.State="failed";p.Message=e.Message;}finally{pool.FreeAll();lock(gate){if(restorePreview?.Id==p.Id)restorePreview=p;}}});
            return new{success=true,restoreId=p.Id};
        }
    }
    public InventoryRestorePlan StageInventoryRestore(string id,InventoryRestoreRequest? expected=null)
    {
        Guard();lock(gate){var p=restorePreview;if(p==null||p.Id!=id||p.State!="preview"||DateTime.UtcNow-p.CreatedUtc>TimeSpan.FromMinutes(15))throw new InvalidOperationException("A fresh successful inventory restore preview is required.");
            if(expected!=null&&expected!=p.Request)throw new InvalidOperationException("Restore options changed; generate a fresh preview.");
            if(pending!=null||restorePending!=null)throw new InvalidOperationException("Another repair is already staged.");
            ValidateRestoreRequest(p.Request);if(p.World!=api.World.SavegameIdentifier||p.Registry!=RestoreRegistry())throw new InvalidOperationException("World or item/block registry changed.");
            p.State="pending";Write(RestorePendingFile,p);restorePending=p;restorePreview=null;return p;}
    }
    private string RestoreReceiptPath(InventoryRestorePlan p)=>Path.Combine(directory,"inventory-receipt-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{p.World}:{p.Request.Dimension}:{p.Request.X}:{p.Request.Y}:{p.Request.Z}")))+".json");
    private sealed record RestoreReceipt(string Plan,DateTime IntentUtc,string Mode,string EntityHash,string SnapshotHash="");
    private static byte[] ReadRestoreChunk(string path,string key)
    {
        using var db=Open(path);using var cmd=db.CreateCommand();cmd.CommandTimeout=2;cmd.CommandText="SELECT data FROM chunk WHERE position=$key";cmd.Parameters.AddWithValue("$key",long.Parse(key,System.Globalization.CultureInfo.InvariantCulture));
        using var row=cmd.ExecuteReader();if(!row.Read())throw new InvalidOperationException("No saved chunk exists at this location.");if(row.GetBytes(0,0,null,0,0)>64*1024*1024)throw new InvalidDataException("Saved chunk exceeds bounds.");return (byte[])row[0];
    }
    private static byte[] PrepareRestore(InventoryRestorePlan p,byte[] blob,string history,string receiptPath,RestoreCatalog catalog,BlockIdChunkCodec codec,bool applying)
    {
        var request=p.Request;ValidateRestoreRequest(request);var entity=codec.EntityAt(blob,request.X,request.Y,request.Z,request.Dimension);var saved=entity.Summary;
        string code=Canonical(saved.SavedBlockCode);
        if(saved.SavedBlockCode.Length==0||!catalog.Blocks.TryGetValue(code,out var target)||!target.Supported||target.Class!=saved.Class)throw new InvalidOperationException("Retained metadata does not identify a supported, registered vanilla container with the same class and original block code.");
        if(saved.InventorySlotCount is not (>=1 and <=64)||saved.SlotsOffset<0||saved.StackListTruncated)throw new InvalidOperationException("A complete standard retained inventory with at most 64 slots is required.");
        int[]? usable=null;
        if(saved.Class=="Bookshelf"&&(saved.InventorySlotCount!=14||saved.Material.Length==0||target.UsableSlots==null||!target.UsableSlots.TryGetValue(saved.ShapeType,out usable)))throw new InvalidOperationException("Bookshelf shape/material or slot layout cannot be verified.");
        int index=((request.Y&31)*32+(request.Z&31))*32+(request.X&31);int source=codec.ReadCells(blob,[index])[index][0];
        if(applying&&(p.EntityHash!=saved.PayloadSha256||p.Source!=source||p.Target!=target.Id||p.TargetCode!=code||p.EntityClass!=saved.Class))throw new InvalidOperationException("Saved container, block or class changed after preview; no restoration was applied.");
        byte[]? slots=null;var receipt=Read<RestoreReceipt>(receiptPath);
        if(request.Mode=="retained"){
            if(saved.NonEmptyStacks==0)throw new InvalidOperationException("No retained item stacks exist to reconnect. Use snapshot mode for an empty container with historical inventory records.");
            if(receipt?.EntityHash==saved.PayloadSha256)throw new InvalidOperationException("This retained inventory has already had a restore intent recorded; inspect the previous result before retrying.");
            if(saved.Stacks.Any(s=>!catalog.KnownIds.Contains((s.Kind,s.Id))))throw new InvalidOperationException("Retained items contain unresolved IDs; restore their mods/mappings first.");
            p.Items=saved.SerializedItemQuantity;p.OccupiedSlots=saved.NonEmptyStacks;p.Fidelity="Retained block-entity payload is preserved byte for byte, including item attributes. Only this position's solid block ID changes.";
        }else{
            if(saved.NonEmptyStacks!=0)throw new InvalidOperationException("Snapshot restoration refuses any non-empty retained inventory. Existing contents are never overwritten.");
            var snapshot=InventoryRestoreHistory.Read(history,p.World,request.X,request.Y,request.Z,request.Dimension,request.SnapshotId);
            if(snapshot.Code!=code||snapshot.SlotCount!=saved.InventorySlotCount)throw new InvalidOperationException("Snapshot block code/slot count does not match retained container metadata.");
            if(receipt!=null&&(snapshot.TimestampUtc<=receipt.IntentUtc||snapshot.Hash==receipt.SnapshotHash))throw new InvalidOperationException("This snapshot predates a previous restore intent at this location; replay is refused.");
            if(applying&&(snapshot.Hash!=p.SnapshotHash||snapshot.TimestampUtc!=p.SnapshotUtc))throw new InvalidOperationException("Historical snapshot changed or was replaced.");
            var tree=new TreeAttribute();foreach(var slot in snapshot.Slots){if(usable!=null&&!usable.Contains(slot.SlotId))throw new InvalidOperationException("Snapshot contains a slot that is unavailable in the retained bookshelf shape.");string itemCode=Canonical(slot.ItemCode);if(!catalog.Items.TryGetValue(itemCode,out var item)||slot.StackSize>item.Maximum)throw new InvalidOperationException("Snapshot contains a missing, ambiguous or oversized item stack: "+itemCode);
                tree.SetItemstack(slot.SlotId.ToString(System.Globalization.CultureInfo.InvariantCulture),new ItemStack{Class=item.Kind,Id=item.Id,StackSize=slot.StackSize});}
            slots=tree.ToBytes();p.SnapshotHash=snapshot.Hash;p.SnapshotUtc=snapshot.TimestampUtc;p.DesiredSlots=snapshot.Slots;p.Items=snapshot.Slots.Sum(s=>(long)s.StackSize);p.OccupiedSlots=snapshot.Slots.Count;p.Fidelity="Historical item codes/quantities restored with default item attributes. Written text, durability, perish timers and bag contents were not logged. Unlogged transfers cannot be detected.";
        }
        var output=codec.RestoreContainer(blob,request.X,request.Y,request.Z,request.Dimension,source,target.Id,saved.PayloadSha256,slots);
        p.Source=source;p.Target=target.Id;p.TargetCode=code;p.EntityClass=saved.Class;p.EntityHash=saved.PayloadSha256;p.Message="Preview restores exactly one saved location on restart. "+(request.CreateBackup?"Automatic world backup will be verified first.":"Automatic backup disabled; your acknowledged manual copy is the recovery path.")+" "+p.Fidelity;return output;
    }
    private void ApplyInventoryRestoreBeforeTerrainLoads()
    {
        var p=restorePending!;bool writes=false;ChunkDataPool? pool=null;
        try{
            Guard();ValidateRestoreRequest(p.Request);if(pending!=null)throw new InvalidOperationException("Conflicting staged jobs; cancel them before restoring.");
            if(p.World!=api.World.SavegameIdentifier||p.Registry!=RestoreRegistry())throw new InvalidOperationException("World or block/item registry changed since preview.");
            var thread=(ChunkServerThread)AccessTools.Field(typeof(ServerMain),"chunkThread").GetValue(api.World)!;
            void Quiescent(){var queue=AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns").GetValue(thread)!;
                if(api.Server.CurrentRunPhase!=EnumServerRunPhase.AssetsFinalize||thread.runOffThreadSaveNow||api.WorldManager.AllLoadedChunks.Count>0||Convert.ToInt32(AccessTools.Property(queue.GetType(),"Count").GetValue(queue))>0)throw new InvalidOperationException("Inventory restore requires startup before terrain loading and background saving.");}
            Quiescent();var db=Database();pool=new ChunkDataPool(32,(ServerMain)api.World){CacheSize=4};var codec=new BlockIdChunkCodec(pool);var catalog=CaptureRestoreCatalog();
            string receipt=RestoreReceiptPath(p);var output=PrepareRestore(p,ReadRestoreChunk(db.DatabaseFilename,p.ChunkKey),historyDatabasePath??Path.Combine(BlockLogger.Services.SuiteDataPaths.GetWorldRoot(api),"blocklogger.db"),receipt,catalog,codec,true);
            if(p.Request.CreateBackup){p.Backup=Path.Combine(directory,"inventory-world-before-"+p.Id+".vcdbs");db.CreateBackup(p.Backup);using var backup=Open(p.Backup);using var check=backup.CreateCommand();check.CommandTimeout=600;check.CommandText="PRAGMA quick_check";if(Convert.ToString(check.ExecuteScalar())!="ok")throw new InvalidDataException("Inventory restore backup failed integrity verification.");}
            else{p.Backup="";api.Logger.Warning("[InventoryRestore] Automatic backup disabled; owner acknowledged a manual world copy for {0}.",p.Id);}
            // Recheck saved bytes and history after backup, then durably reserve this
            // location before the single chunk write. Uncertain intents never replay.
            output=PrepareRestore(p,ReadRestoreChunk(db.DatabaseFilename,p.ChunkKey),historyDatabasePath??Path.Combine(BlockLogger.Services.SuiteDataPaths.GetWorldRoot(api),"blocklogger.db"),receipt,catalog,codec,true);Quiescent();
            p.State="applying";Write(RestoreHistoryFile,p);Write(receipt,new RestoreReceipt(p.Id,DateTime.UtcNow,p.Request.Mode,p.EntityHash,p.SnapshotHash));File.Move(RestorePendingFile,Path.Combine(directory,"inventory-consumed-"+p.Id+".json"),true);restorePending=null;
            writes=true;db.SetChunks([new DbChunk(ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)long.Parse(p.ChunkKey,System.Globalization.CultureInfo.InvariantCulture))),output)]);
            p.Changed=true;p.State="complete";p.Message="One-location inventory restoration completed. "+p.Fidelity;api.Logger.Notification("[InventoryRestore] Completed {0}: {1} items at {2},{3},{4} dimension {5}; backup {6}.",p.Request.Mode,p.Items,p.Request.X,p.Request.Y,p.Request.Z,p.Request.Dimension,p.Backup);
        }catch(Exception e){p.State=writes?"failed-uncertain":"failed";p.Message=e.Message+(writes?" A write was attempted; inspect/restore your backup before continuing. The receipt prevents automatic replay.":" No terrain or inventory edits were made.");api.Logger.Error("[InventoryRestore] {0}",p.Message);
            if(File.Exists(RestorePendingFile))File.Move(RestorePendingFile,Path.Combine(directory,"inventory-rejected-"+p.Id+".json"),true);restorePending=null;if(writes)((ServerMain)api.World).Stop("Inventory restoration failed; inspect backup and restore receipt",EnumExitMode.SoftExit);
        }finally{pool?.FreeAll();restoreLast=p;Write(RestoreHistoryFile,p);}
    }
}
