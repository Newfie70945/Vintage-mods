using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace BlockLogger.Services;

public sealed partial class CustomPatchService
{
    public byte[] ExportContentPatchMod()
    {
        lock (sync)
        {
            string folder = GetGeneratedContentModPath();
            if (!File.Exists(Path.Combine(folder, "modinfo.json"))) throw new InvalidOperationException("Save a content patch before exporting the pack.");
            using var output = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (string relative in new[] { "modinfo.json", "assets/" + ContentPatchModId + "/patches/generated.json" })
                {
                    var entry = zip.CreateEntry(relative, System.IO.Compression.CompressionLevel.Optimal);
                    using var target = entry.Open();
                    using var input = File.OpenRead(Path.Combine(folder, relative)); input.CopyTo(target);
                }
            return output.ToArray();
        }
    }
    private ContentAssetEntry[]? contentAssetCatalog;
    public object GetContentAssets(string kind, string search)
    {
        lock (sync)
        {
            contentAssetCatalog ??= api.Assets.AllAssets.Keys
                .Where(x => ContentKind(x.Path) != "")
                .Select(x => new ContentAssetEntry { File = x.ToString(), Kind = ContentKind(x.Path) })
                .OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase).ToArray();
            search = (search ?? "").Trim();
            if (search.Length > 160) search = search[..160];
            var matches = contentAssetCatalog.Where(x => (string.IsNullOrEmpty(kind) || x.Kind == kind) &&
                (search.Length == 0 || x.File.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
            return new { assets = matches.Take(200).ToArray(), totalMatches = matches.Length, limit = 200 };
        }
    }
    private static string ContentKind(string path) => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "" :
        path.StartsWith("recipes/", StringComparison.OrdinalIgnoreCase) ? "recipe" :
        path.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase) ? "item" :
        path.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase) ? "block" : "";

    private JToken ReadContentAsset(string file)
    {
        if (file.Length > 500 || !file.Contains(':') || file.Contains('*') || file.Contains("..") || file.Contains('\\'))
            throw new InvalidOperationException("Choose a loaded recipe, item or block asset.");
        var location = new AssetLocation(file);
        if (ContentKind(location.Path) == "" || !api.Assets.AllAssets.TryGetValue(location, out var asset))
            throw new InvalidOperationException("The selected asset is not loaded on this server.");
        byte[] bytes = asset.Data;
        if (bytes.Length == 0 || bytes.Length > 1024 * 1024)
            throw new InvalidOperationException("This asset exceeds the generator's 1 MiB inspection limit; use the advanced editor.");
        // Vintage Story assets allow comments, unquoted names and trailing commas.
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'))) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        return JToken.Load(reader);
    }

    public ContentAssetInspection InspectContentAsset(string file)
    {
        lock (sync)
        {
            var document = ReadContentAsset(file);
            var result = new ContentAssetInspection { File = file, Kind = ContentKind(new AssetLocation(file).Path) };
            if (document is JArray array)
            {
                for (int i = 0; i < array.Count && i < 200; i++)
                    if (array[i] is JObject row) result.Roots.Add(BuildRoot(row, "/" + i, i));
            }
            else if (document is JObject root) result.Roots.Add(BuildRoot(root, "", 0));
            else throw new InvalidOperationException("The selected asset has no editable object definitions.");
            if (result.Roots.Count == 0) throw new InvalidOperationException("This file has no editable object definitions; use the advanced editor.");
            CollectFields(document, "", result.Fields, 0);
            result.FieldsTruncated = result.Fields.Count >= 600;
            return result;
        }
    }
    private static ContentAssetRoot BuildRoot(JObject row, string path, int index)
    {
        var root = new ContentAssetRoot { Path = path, Label = ScalarText(row["name"]) ?? ScalarText(row["code"]) ??
            ScalarText((row["output"] as JObject)?["code"]) ?? "Definition " + (index + 1) };
        var ingredients = row["ingredients"];
        if (ingredients is JObject keyed)
        {
            foreach (var property in keyed.Properties())
                if (property.Value is JObject ingredient && ScalarText(ingredient["code"]) is string code)
                    root.Ingredients.Add(new ContentIngredient { Key = property.Name, Code = code });
        }
        else if (ingredients is JArray indexed)
        {
            for (int i = 0; i < indexed.Count; i++)
                if (indexed[i] is JObject ingredient && ScalarText(ingredient["code"]) is string code)
                    root.Ingredients.Add(new ContentIngredient { Key = i.ToString(CultureInfo.InvariantCulture), Code = code });
        }
        return root;
    }
    private static string? ScalarText(JToken? value) => value is JValue && value.Type != JTokenType.Null ? value.ToString() : null;
    private static void CollectFields(JToken token, string path, List<ContentAssetField> fields, int depth)
    {
        // Prefer shallow properties: long variant/texture arrays must not hide ordinary
        // balance fields later in the asset. Bound both queued work and copied JSON values.
        var queue = new Queue<(JToken Token, string Path, int Depth)>();
        queue.Enqueue((token, path, depth));
        int copiedCharacters = 0;
        while (queue.Count > 0 && fields.Count < 600)
        {
            var current = queue.Dequeue();
            if (current.Path.Length > 0)
            {
                string json = current.Token.ToString(Formatting.None);
                bool copy = json.Length <= 8192 && copiedCharacters + json.Length <= 131072;
                fields.Add(new ContentAssetField { Path = current.Path, Type = current.Token.Type.ToString(),
                    ValueJson = copy ? json : "", Summary = json.Length <= 160 ? json : json[..160] + "…" });
                if (copy) copiedCharacters += json.Length;
            }
            if (current.Depth >= 10) continue;
            if (current.Token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (queue.Count >= 2048) break;
                    queue.Enqueue((property.Value, current.Path + "/" + EscapeJsonPointerToken(property.Name), current.Depth + 1));
                }
            }
            else if (current.Token is JArray array)
                for (int i = 0; i < array.Count && queue.Count < 2048; i++)
                    queue.Enqueue((array[i], current.Path + "/" + i, current.Depth + 1));
        }
    }

    private ContentPatchActionResult GenerateGuidedContentPatch(ContentPatchGenerateRequest request)
    {
        lock (sync)
        {
            try
            {
                JToken document = ReadContentAsset(request.File ?? "");
                string rootPath = request.RootPath ?? "";
                if (ResolvePointer(document, rootPath) is not JObject) throw new InvalidOperationException("Choose a recipe or asset definition first.");
                string preset = (request.Preset ?? "property").ToLowerInvariant();
                string ingredient = EscapeJsonPointerToken(request.IngredientKey ?? "");
                string path = preset switch
                {
                    "recipe-output-quantity" => rootPath + "/output/quantity",
                    "recipe-output-code" => rootPath + "/output/code",
                    "recipe-ingredient-quantity" => rootPath + "/ingredients/" + ingredient + "/quantity",
                    "recipe-ingredient-code" => rootPath + "/ingredients/" + ingredient + "/code",
                    "recipe-enabled" => rootPath + "/enabled",
                    "item-stacksize" => rootPath + "/maxStackSize",
                    "property" => request.Path ?? "",
                    _ => throw new InvalidOperationException("Unknown generator change type.")
                };
                string kind = ContentKind(new AssetLocation(request.File).Path);
                if (!path.StartsWith(rootPath + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Choose a property inside the selected definition.");
                if (preset.StartsWith("recipe-") && kind != "recipe" || preset == "item-stacksize" && kind != "item")
                    throw new InvalidOperationException("This change type does not match the selected asset.");
                string value = request.ValueJson ?? "null";
                if (preset.EndsWith("quantity") || preset == "item-stacksize")
                {
                    if (request.Quantity < 1 || request.Quantity > 100000) throw new InvalidOperationException("Quantity must be between 1 and 100,000.");
                    value = request.Quantity.ToString(CultureInfo.InvariantCulture);
                }
                else if (preset.EndsWith("code"))
                {
                    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 300) throw new InvalidOperationException("Enter an item or block code.");
                    value = System.Text.Json.JsonSerializer.Serialize(request.Code.Trim());
                }
                else if (preset == "recipe-enabled") value = request.Enabled ? "true" : "false";
                if (string.IsNullOrWhiteSpace(value) && !(preset == "property" && request.Operation == "remove"))
                    throw new InvalidOperationException("Enter a new value before generating the patch.");
                var patch = new ContentPatchDefinition { Id = request.Id ?? "", Name = request.Name ?? "", File = request.File ?? "",
                    Path = path, Operation = preset == "property" ? request.Operation : "add", ValueJson = value,
                    Side = NormalizeSide(request.Side), Notes = request.Notes ?? "", DependsOnModId = request.DependsOnModId ?? "" };
                if (string.IsNullOrWhiteSpace(patch.Name)) patch.Name = "Balance " + new AssetLocation(patch.File).Path.Split('/').Last();
                if (patch.ValueJson.Length > 131072) throw new InvalidOperationException("The patch value is too large.");
                patch.Clamp();
                string? error = ValidateContentPatch(patch);
                if (error != null) throw new InvalidOperationException(error);
                if (patch.Operation is not ("add" or "replace" or "remove")) throw new InvalidOperationException("Guided changes support add, replace or remove; use the advanced editor for other operations.");
                string[] parts = PointerParts(patch.Path);
                string parentPath = patch.Path[..patch.Path.LastIndexOf('/')];
                JToken parent = ResolvePointer(document, parentPath) ?? throw new InvalidOperationException("The parent property does not exist. Choose an existing field or use the advanced editor.");
                string key = parts[^1];
                JToken? before = ResolvePointer(document, patch.Path);
                if (patch.Operation != "add" && before == null) throw new InvalidOperationException("This field does not exist; replace/remove cannot be applied.");
                JToken? replacement = patch.Operation == "remove" ? null : JToken.Parse(patch.ValueJson);
                if (parent is JObject objectParent)
                { if (replacement == null) objectParent.Remove(key); else objectParent[key] = replacement; }
                else if (parent is JArray list && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 0 && index <= list.Count)
                {
                    if (patch.Operation == "add") list.Insert(index, replacement!);
                    else if (index < list.Count) { if (replacement == null) list.RemoveAt(index); else list[index] = replacement; }
                    else throw new InvalidOperationException("Array index is outside this asset.");
                }
                else throw new InvalidOperationException("The selected parent is not an object or array.");
                var native = new JObject { ["file"] = patch.File, ["op"] = patch.Operation, ["path"] = patch.Path };
                if (patch.Side.Length > 0) native["side"] = patch.Side;
                if (replacement != null) native["value"] = replacement.DeepClone();
                if (patch.DependsOnModId.Length > 0) native["dependsOn"] = new JArray(new JObject { ["modid"] = patch.DependsOnModId });
                return new ContentPatchActionResult { Success = true, Message = "Patch generated and checked against the loaded asset.", Generated = patch,
                    Preview = new ContentPatchPreview { BeforeJson = before?.ToString(Formatting.Indented) ?? "(not present)",
                        AfterJson = replacement?.ToString(Formatting.Indented) ?? "(removed)", PatchJson = new JArray(native).ToString(Formatting.Indented),
                        Note = "Preview uses the currently loaded asset. Other patch ordering and mod code can affect the result after restart." +
                            (parent is JArray && patch.Operation == "add" ? " Adding to an array inserts an entry and shifts later entries." : "") } };
            }
            catch (Exception ex) { return new ContentPatchActionResult { Success = false, Message = ex.Message }; }
        }
    }
    public ContentPatchActionResult SaveGeneratedContentPatch(ContentPatchGenerateRequest request)
    {
        lock (sync)
        {
            request.Guided = true;
            var generated = GenerateGuidedContentPatch(request);
            if (!generated.Success || generated.Generated == null) return generated;
            generated.Generated.Enabled = request.Activate;
            return SaveContentPatch(generated.Generated);
        }
    }
    private static string[] PointerParts(string path)
    {
        if (path.Length == 0) return Array.Empty<string>();
        if (!path.StartsWith('/')) throw new InvalidOperationException("The property path must start with '/'.");
        var parts = path[1..].Split('/');
        if (parts.Any(x => System.Text.RegularExpressions.Regex.IsMatch(x, "~(?![01])"))) throw new InvalidOperationException("Invalid JSON pointer escape.");
        return parts.Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
    }
    private static JToken? ResolvePointer(JToken root, string path)
    {
        JToken? current = root;
        foreach (string part in PointerParts(path))
        {
            if (current is JObject obj) current = obj[part];
            else if (current is JArray list && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < list.Count) current = list[index];
            else return null;
        }
        return current;
    }
}
public sealed class ContentAssetEntry { public string File { get; set; } = ""; public string Kind { get; set; } = ""; }
public sealed class ContentAssetInspection
{
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool FieldsTruncated { get; set; }
    public List<ContentAssetRoot> Roots { get; set; } = new();
    public List<ContentAssetField> Fields { get; set; } = new();
}
public sealed class ContentAssetRoot { public string Path { get; set; } = ""; public string Label { get; set; } = ""; public List<ContentIngredient> Ingredients { get; set; } = new(); }
public sealed class ContentIngredient { public string Key { get; set; } = ""; public string Code { get; set; } = ""; }
public sealed class ContentAssetField { public string Path { get; set; } = ""; public string Type { get; set; } = ""; public string ValueJson { get; set; } = ""; public string Summary { get; set; } = ""; }
public sealed class ContentPatchPreview { public string BeforeJson { get; set; } = ""; public string AfterJson { get; set; } = ""; public string PatchJson { get; set; } = ""; public string Note { get; set; } = ""; }
