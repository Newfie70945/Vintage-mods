using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace BlockLogger.WorldRepair;

// Read only the inspected built-in attribute wire formats. Never instantiate a
// mod block entity/attribute, resolve an ItemStack, or intern arbitrary save keys.
public static class SavedBlockEntityInspector
{
    public sealed class StackSummary
    {
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Id { get; set; }
        public int Quantity { get; set; }
        public string CurrentCode { get; set; } = "";
        public bool InInventoryTree { get; set; }
    }
    public sealed class EntitySummary
    {
        public string Class { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore] public int MaterialIdsOffset {get;set;}=-1;
        [System.Text.Json.Serialization.JsonIgnore] public int[]? MaterialIds {get;set;}
        public string SavedBlockCode { get; set; } = "";
        public int? X { get; set; }
        public int? InternalY { get; set; }
        public int? Z { get; set; }
        public int? Y => InternalY.HasValue ? InternalY.Value % 32768 : null;
        public int? Dimension => InternalY.HasValue ? InternalY.Value / 32768 : null;
        public int SerializedBytes { get; set; }
        public string PayloadSha256 { get; set; } = "";
        public bool Complete { get; set; }
        public string Issue { get; set; } = "";
        public List<string> InventoryPaths { get; set; } = [];
        public int NonEmptyStacks { get; set; }
        public long SerializedItemQuantity { get; set; }
        public List<StackSummary> Stacks { get; set; } = [];
        public bool StackListTruncated => NonEmptyStacks > Stacks.Count;
        public int? SolidId { get; set; }
        public string SolidCode { get; set; } = "";
        public string CurrentEntityClass { get; set; } = "";
        public string Assessment { get; set; } = "";
        public string ShapeType { get; set; } = "";
        public string Material { get; set; } = "";
        public int? InventorySlotCount { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public int BlockCodeOffset { get; set; } = -1;
        [System.Text.Json.Serialization.JsonIgnore] public int BlockCodeLength { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public int SlotsOffset { get; set; } = -1;
        [System.Text.Json.Serialization.JsonIgnore] public int SlotsLength { get; set; }

    }
    public static bool BuiltinTypesSupported()
    {
        string[] names=["IntAttribute","LongAttribute","DoubleAttribute","FloatAttribute","StringAttribute","TreeAttribute","ItemstackAttribute","ByteArrayAttribute","BoolAttribute","StringArrayAttribute","IntArrayAttribute","FloatArrayAttribute","DoubleArrayAttribute","TreeArrayAttribute","LongArrayAttribute","BoolArrayAttribute"];
        for(int i=0;i<names.Length;i++)if(!TreeAttribute.AttributeIdMapping.TryGetValue(i+1,out var type)||type.Assembly!=typeof(TreeAttribute).Assembly||type.FullName!="Vintagestory.API.Datastructures."+names[i])return false;
        return true;
    }
    public static EntitySummary Inspect(byte[] bytes)
    {
        var summary = new EntitySummary { SerializedBytes = bytes.Length, PayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        try
        {
            if (!BuiltinTypesSupported()) throw new InvalidDataException("A mod changed built-in attribute serialization; retained data cannot be verified safely.");
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Block entity exceeds the 2 MiB inspection limit.");
            using var input = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
            var parser = new Parser(reader, summary);
            summary.Class = parser.Text();
            parser.Tree("", 0);
            if (input.Position != input.Length) throw new InvalidDataException("Trailing block-entity data; format cannot be verified.");
            if (!summary.X.HasValue || !summary.InternalY.HasValue || !summary.Z.HasValue) throw new InvalidDataException("Saved block-entity position is absent.");
            summary.Complete = true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OverflowException or FormatException)
        { summary.Issue = e.Message; }
        return summary;
    }
    private sealed class Parser(BinaryReader reader, EntitySummary result)
    {
        private int nodes;
        private void Skip(long length)
        {
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Truncated attribute payload.");
            reader.BaseStream.Position += length;
        }
        private int Count(int maximum = 1048576)
        {
            int n = reader.ReadInt32();
            if (n < 0 || n > maximum) throw new InvalidDataException("Attribute array exceeds the inspection limit.");
            return n;
        }
        public string Text()
        {
            int length = reader.Read7BitEncodedInt();
            if (length < 0 || length > 4096 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Attribute string exceeds the inspection limit or is truncated.");
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
        }
        public void Tree(string path, int depth)
        {
            if (depth > 20) throw new InvalidDataException("Attribute nesting exceeds the inspection limit.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                int type = reader.ReadByte(); if (type == 0) return;
                if (++nodes > 16384) throw new InvalidDataException("Attribute count exceeds the inspection limit.");
                string key = Text();
                if (!keys.Add(key)) throw new InvalidDataException("Duplicate attribute key; ambiguous saved data.");
                string current = path.Length == 0 ? key : path + "/" + key;
                if (current.Length > 2048) throw new InvalidDataException("Attribute path exceeds the inspection limit.");
                switch (type)
                {
                    case 1:
                        int value = reader.ReadInt32();
                        if (current == "inventory/qslots") result.InventorySlotCount = value;
                        if (depth == 0) { if (key == "posx") result.X = value; if (key == "posy") result.InternalY = value; if (key == "posz") result.Z = value; }
                        break;
                    case 2: case 3: Skip(8); break;
                    case 4: Skip(4); break;
                    case 5: int textStart=checked((int)reader.BaseStream.Position);string text = Text(); if (depth == 0){if(key == "blockCode"){result.SavedBlockCode = text;result.BlockCodeOffset=textStart;result.BlockCodeLength=checked((int)reader.BaseStream.Position)-textStart;}if(key=="type") result.ShapeType=text;if(key=="material") result.Material=text;} break;
                    case 6:
                        if ((key.Contains("inventory", StringComparison.OrdinalIgnoreCase) || key == "inv") && result.InventoryPaths.Count < 32) result.InventoryPaths.Add(current);
                        int start = checked((int)reader.BaseStream.Position);
                        Tree(current, depth + 1);
                        if(current=="inventory/slots"){result.SlotsOffset=start;result.SlotsLength=checked((int)reader.BaseStream.Position)-start;}
                        break;
                    case 7:
                        if (!reader.ReadBoolean())
                        {
                            int kind = reader.ReadInt32(), id = reader.ReadInt32(), quantity = reader.ReadInt32();
                            if (quantity > 0)
                            {
                                result.NonEmptyStacks++; result.SerializedItemQuantity += quantity;
                                if (result.Stacks.Count < 64) result.Stacks.Add(new StackSummary { Path = current, Kind = kind == (int)EnumItemClass.Block ? "Block" : kind == (int)EnumItemClass.Item ? "Item" : "Unknown", Id = id, Quantity = quantity, InInventoryTree = result.InventoryPaths.Any(p => current.StartsWith(p + "/", StringComparison.Ordinal)) });
                            }
                            Tree(current + "/attributes", depth + 1);
                        }
                        break;
                    case 8: Skip(reader.ReadInt16()); break;
                    case 16: Skip(Count()); break;
                    case 9: Skip(1); break;
                    case 10: int strings = Count(4096); for (int i = 0; i < strings; i++) Text(); break;
                    case 11:
                        int ints=Count();
                        if(depth==0&&key=="materials"&&result.Class=="MicroBlock")
                        {
                            if(ints>256)throw new InvalidDataException("Chiseled material palette exceeds 256 entries.");
                            result.MaterialIdsOffset=checked((int)reader.BaseStream.Position);result.MaterialIds=new int[ints];
                            for(int i=0;i<ints;i++)result.MaterialIds[i]=reader.ReadInt32();
                        }
                        else Skip((long)ints*4);
                        break;
                    case 12: Skip((long)Count() * 4); break;
                    case 13: case 15: Skip((long)Count() * 8); break;
                    case 14: int trees = Count(1024); for (int i = 0; i < trees; i++) Tree(current + "[" + i + "]", depth + 1); break;
                    default: throw new InvalidDataException("Unsupported/custom attribute type " + type + "; inspection is partial.");
                }
            }
        }
    }
}
