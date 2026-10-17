using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using BlockLogger.ServerHealth;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class CustomPatchService : IDisposable
{
	private sealed class RuntimePatchState
	{
		public long Calls;

		public long Skipped;

		public long TotalElapsedTicks;

		public long MaxElapsedTicks;

		public long LastAllowedTicks;

		public string Id { get; }

		public string Mode { get; }

		public int ThrottleMilliseconds { get; }

		public RuntimePatchState(string id, string mode, int throttleMilliseconds)
		{
			Id = id;
			Mode = mode;
			ThrottleMilliseconds = throttleMilliseconds;
		}
	}

	private ContentAssetEntry[]? contentAssetCatalog;

	public const string ConfigFile = "NewfiesCustomPatches.json";

	public const string ContentConfigFile = "NewfiesContentPatches.json";

	private const string ContentPatchModId = "newfiessuitecontentpatches";

	private const string ContentPatchFolderName = "NewfiesSuiteContentPatches";

	private const string HarmonyId = "newfies.serveressentials.custompatches";

	private readonly ICoreServerAPI api;

	private readonly Harmony harmony = new Harmony("newfies.serveressentials.custompatches");

	private readonly object sync = new object();

	private CustomPatchStore store = new CustomPatchStore();

	private ContentPatchStore contentStore = new ContentPatchStore();

	private bool contentRestartRequired;

	private static CustomPatchService? current;

	private readonly ConcurrentDictionary<MethodBase, RuntimePatchState> runtime = new ConcurrentDictionary<MethodBase, RuntimePatchState>();

	public byte[] ExportContentPatchMod()
	{
		lock (sync)
		{
			string generatedContentModPath = GetGeneratedContentModPath();
			if (!File.Exists(Path.Combine(generatedContentModPath, "modinfo.json")))
			{
				throw new InvalidOperationException("Save a content patch before exporting the pack.");
			}
			using MemoryStream memoryStream = new MemoryStream();
			using (ZipArchive zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
			{
				string[] array = new string[2] { "modinfo.json", "assets/newfiessuitecontentpatches/patches/generated.json" };
				foreach (string text in array)
				{
					ZipArchiveEntry zipArchiveEntry = zipArchive.CreateEntry(text, CompressionLevel.Optimal);
					using Stream destination = zipArchiveEntry.Open();
					using FileStream fileStream = File.OpenRead(Path.Combine(generatedContentModPath, text));
					fileStream.CopyTo(destination);
				}
			}
			return memoryStream.ToArray();
		}
	}

	public object GetContentAssets(string kind, string search)
	{
		lock (sync)
		{
			if (contentAssetCatalog == null)
			{
				contentAssetCatalog = (from x in api.Assets.AllAssets.Keys
					where ContentKind(x.Path) != ""
					select new ContentAssetEntry
					{
						File = x.ToString(),
						Kind = ContentKind(x.Path)
					}).OrderBy((ContentAssetEntry x) => x.File, StringComparer.OrdinalIgnoreCase).ToArray();
			}
			search = (search ?? "").Trim();
			if (search.Length > 160)
			{
				search = search.Substring(0, 160);
			}
			ContentAssetEntry[] array = contentAssetCatalog.Where((ContentAssetEntry x) => (string.IsNullOrEmpty(kind) || x.Kind == kind) && (search.Length == 0 || x.File.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
			return new
			{
				assets = array.Take(200).ToArray(),
				totalMatches = array.Length,
				limit = 200
			};
		}
	}

	private static string ContentKind(string path)
	{
		string result;
		if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			if (path.StartsWith("recipes/", StringComparison.OrdinalIgnoreCase))
			{
				result = "recipe";
			}
			else if (path.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase))
			{
				result = "item";
			}
			else
			{
				result = (path.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase) ? "block" : "");
			}
		}
		else
		{
			result = "";
		}
		return result;
	}

	private JToken ReadContentAsset(string file)
	{
		if (file.Length > 500 || !file.Contains(':') || file.Contains('*') || file.Contains("..") || file.Contains('\\'))
		{
			throw new InvalidOperationException("Choose a loaded recipe, item or block asset.");
		}
		AssetLocation assetLocation = new AssetLocation(file);
		if (ContentKind(assetLocation.Path) == "" || !api.Assets.AllAssets.TryGetValue(assetLocation, out var value))
		{
			throw new InvalidOperationException("The selected asset is not loaded on this server.");
		}
		byte[] data = value.Data;
		if (data.Length == 0 || data.Length > 1048576)
		{
			throw new InvalidOperationException("This asset exceeds the generator's 1 MiB inspection limit; use the advanced editor.");
		}
		using JsonTextReader reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(data).TrimStart('\ufeff')))
		{
			MaxDepth = 64,
			DateParseHandling = DateParseHandling.None
		};
		return JToken.Load(reader);
	}

	public ContentAssetInspection InspectContentAsset(string file)
	{
		lock (sync)
		{
			JToken jToken = ReadContentAsset(file);
			ContentAssetInspection contentAssetInspection = new ContentAssetInspection
			{
				File = file,
				Kind = ContentKind(new AssetLocation(file).Path)
			};
			if (jToken is JArray jArray)
			{
				for (int i = 0; i < jArray.Count && i < 200; i++)
				{
					if (jArray[i] is JObject row)
					{
						contentAssetInspection.Roots.Add(BuildRoot(row, "/" + i, i));
					}
				}
			}
			else
			{
				if (!(jToken is JObject row2))
				{
					throw new InvalidOperationException("The selected asset has no editable object definitions.");
				}
				contentAssetInspection.Roots.Add(BuildRoot(row2, "", 0));
			}
			if (contentAssetInspection.Roots.Count == 0)
			{
				throw new InvalidOperationException("This file has no editable object definitions; use the advanced editor.");
			}
			CollectFields(jToken, "", contentAssetInspection.Fields, 0);
			contentAssetInspection.FieldsTruncated = contentAssetInspection.Fields.Count >= 600;
			return contentAssetInspection;
		}
	}

	private static ContentAssetRoot BuildRoot(JObject row, string path, int index)
	{
		ContentAssetRoot contentAssetRoot = new ContentAssetRoot();
		contentAssetRoot.Path = path;
		contentAssetRoot.Label = ScalarText(row["name"]) ?? ScalarText(row["code"]) ?? ScalarText((row["output"] as JObject)?["code"]) ?? ("Definition " + (index + 1));
		ContentAssetRoot contentAssetRoot2 = contentAssetRoot;
		JToken jToken = row["ingredients"];
		if (jToken is JObject jObject)
		{
			foreach (JProperty item in jObject.Properties())
			{
				if (item.Value is JObject jObject2)
				{
					string text = ScalarText(jObject2["code"]);
					if (text != null)
					{
						contentAssetRoot2.Ingredients.Add(new ContentIngredient
						{
							Key = item.Name,
							Code = text
						});
					}
				}
			}
		}
		else if (jToken is JArray jArray)
		{
			for (int i = 0; i < jArray.Count; i++)
			{
				if (jArray[i] is JObject jObject3)
				{
					string text2 = ScalarText(jObject3["code"]);
					if (text2 != null)
					{
						contentAssetRoot2.Ingredients.Add(new ContentIngredient
						{
							Key = i.ToString(CultureInfo.InvariantCulture),
							Code = text2
						});
					}
				}
			}
		}
		return contentAssetRoot2;
	}

	private static string? ScalarText(JToken? value)
	{
		return (value is JValue && value.Type != JTokenType.Null) ? value.ToString() : null;
	}

	private static void CollectFields(JToken token, string path, List<ContentAssetField> fields, int depth)
	{
		Queue<(JToken, string, int)> queue = new Queue<(JToken, string, int)>();
		queue.Enqueue((token, path, depth));
		int num = 0;
		while (queue.Count > 0 && fields.Count < 600)
		{
			(JToken, string, int) tuple = queue.Dequeue();
			if (tuple.Item2.Length > 0)
			{
				string text = tuple.Item1.ToString(Formatting.None, Array.Empty<JsonConverter>());
				bool flag = text.Length <= 8192 && num + text.Length <= 131072;
				fields.Add(new ContentAssetField
				{
					Path = tuple.Item2,
					Type = tuple.Item1.Type.ToString(),
					ValueJson = (flag ? text : ""),
					Summary = ((text.Length <= 160) ? text : (text.Substring(0, 160) + "…"))
				});
				if (flag)
				{
					num += text.Length;
				}
			}
			if (tuple.Item3 >= 10)
			{
				continue;
			}
			if (tuple.Item1 is JObject jObject)
			{
				foreach (JProperty item in jObject.Properties())
				{
					if (queue.Count >= 2048)
					{
						break;
					}
					queue.Enqueue((item.Value, tuple.Item2 + "/" + EscapeJsonPointerToken(item.Name), tuple.Item3 + 1));
				}
			}
			else
			{
				if (!(tuple.Item1 is JArray jArray))
				{
					continue;
				}
				for (int i = 0; i < jArray.Count; i++)
				{
					if (queue.Count >= 2048)
					{
						break;
					}
					queue.Enqueue((jArray[i], tuple.Item2 + "/" + i, tuple.Item3 + 1));
				}
			}
		}
	}

	private ContentPatchActionResult GenerateGuidedContentPatch(ContentPatchGenerateRequest request)
	{
		lock (sync)
		{
			try
			{
				JToken root = ReadContentAsset(request.File ?? "");
				string text = request.RootPath ?? "";
				if (!(ResolvePointer(root, text) is JObject))
				{
					throw new InvalidOperationException("Choose a recipe or asset definition first.");
				}
				string text2 = (request.Preset ?? "property").ToLowerInvariant();
				string text3 = EscapeJsonPointerToken(request.IngredientKey ?? "");
				if (1 == 0)
				{
				}
				string text4 = text2 switch
				{
					"recipe-output-quantity" => text + "/output/quantity", 
					"recipe-output-code" => text + "/output/code", 
					"recipe-ingredient-quantity" => text + "/ingredients/" + text3 + "/quantity", 
					"recipe-ingredient-code" => text + "/ingredients/" + text3 + "/code", 
					"recipe-enabled" => text + "/enabled", 
					"item-stacksize" => text + "/maxStackSize", 
					"property" => request.Path ?? "", 
					_ => throw new InvalidOperationException("Unknown generator change type."), 
				};
				if (1 == 0)
				{
				}
				string text5 = text4;
				string text6 = ContentKind(new AssetLocation(request.File).Path);
				if (!text5.StartsWith(text + "/", StringComparison.Ordinal))
				{
					throw new InvalidOperationException("Choose a property inside the selected definition.");
				}
				if ((text2.StartsWith("recipe-") && text6 != "recipe") || (text2 == "item-stacksize" && text6 != "item"))
				{
					throw new InvalidOperationException("This change type does not match the selected asset.");
				}
				string text7 = request.ValueJson ?? "null";
				if (text2.EndsWith("quantity") || text2 == "item-stacksize")
				{
					if (request.Quantity < 1 || request.Quantity > 100000)
					{
						throw new InvalidOperationException("Quantity must be between 1 and 100,000.");
					}
					text7 = request.Quantity.ToString(CultureInfo.InvariantCulture);
				}
				else if (text2.EndsWith("code"))
				{
					if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 300)
					{
						throw new InvalidOperationException("Enter an item or block code.");
					}
					text7 = System.Text.Json.JsonSerializer.Serialize(request.Code.Trim());
				}
				else if (text2 == "recipe-enabled")
				{
					text7 = (request.Enabled ? "true" : "false");
				}
				if (string.IsNullOrWhiteSpace(text7) && (!(text2 == "property") || !(request.Operation == "remove")))
				{
					throw new InvalidOperationException("Enter a new value before generating the patch.");
				}
				ContentPatchDefinition contentPatchDefinition = new ContentPatchDefinition
				{
					Id = (request.Id ?? ""),
					Name = (request.Name ?? ""),
					File = (request.File ?? ""),
					Path = text5,
					Operation = ((text2 == "property") ? request.Operation : "add"),
					ValueJson = text7,
					Side = NormalizeSide(request.Side),
					Notes = (request.Notes ?? ""),
					DependsOnModId = (request.DependsOnModId ?? "")
				};
				if (string.IsNullOrWhiteSpace(contentPatchDefinition.Name))
				{
					contentPatchDefinition.Name = "Balance " + new AssetLocation(contentPatchDefinition.File).Path.Split('/').Last();
				}
				if (contentPatchDefinition.ValueJson.Length > 131072)
				{
					throw new InvalidOperationException("The patch value is too large.");
				}
				contentPatchDefinition.Clamp();
				string text8 = ValidateContentPatch(contentPatchDefinition);
				if (text8 != null)
				{
					throw new InvalidOperationException(text8);
				}
				bool flag;
				switch (contentPatchDefinition.Operation)
				{
				case "add":
				case "replace":
				case "remove":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (!flag)
				{
					throw new InvalidOperationException("Guided changes support add, replace or remove; use the advanced editor for other operations.");
				}
				string[] array = PointerParts(contentPatchDefinition.Path);
				string path = contentPatchDefinition.Path.Substring(0, contentPatchDefinition.Path.LastIndexOf('/'));
				JToken jToken = ResolvePointer(root, path) ?? throw new InvalidOperationException("The parent property does not exist. Choose an existing field or use the advanced editor.");
				string text9 = array[^1];
				JToken jToken2 = ResolvePointer(root, contentPatchDefinition.Path);
				if (contentPatchDefinition.Operation != "add" && jToken2 == null)
				{
					throw new InvalidOperationException("This field does not exist; replace/remove cannot be applied.");
				}
				JToken jToken3 = ((contentPatchDefinition.Operation == "remove") ? null : JToken.Parse(contentPatchDefinition.ValueJson));
				if (jToken is JObject jObject)
				{
					if (jToken3 == null)
					{
						jObject.Remove(text9);
					}
					else
					{
						jObject[text9] = jToken3;
					}
				}
				else
				{
					if (!(jToken is JArray jArray) || !int.TryParse(text9, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0 || result > jArray.Count)
					{
						throw new InvalidOperationException("The selected parent is not an object or array.");
					}
					if (contentPatchDefinition.Operation == "add")
					{
						jArray.Insert(result, jToken3);
					}
					else
					{
						if (result >= jArray.Count)
						{
							throw new InvalidOperationException("Array index is outside this asset.");
						}
						if (jToken3 == null)
						{
							jArray.RemoveAt(result);
						}
						else
						{
							jArray[result] = jToken3;
						}
					}
				}
				JObject jObject2 = new JObject
				{
					["file"] = contentPatchDefinition.File,
					["op"] = contentPatchDefinition.Operation,
					["path"] = contentPatchDefinition.Path
				};
				if (contentPatchDefinition.Side.Length > 0)
				{
					jObject2["side"] = contentPatchDefinition.Side;
				}
				if (jToken3 != null)
				{
					jObject2["value"] = jToken3.DeepClone();
				}
				if (contentPatchDefinition.DependsOnModId.Length > 0)
				{
					jObject2["dependsOn"] = new JArray(new JObject { ["modid"] = contentPatchDefinition.DependsOnModId });
				}
				ContentPatchActionResult contentPatchActionResult = new ContentPatchActionResult();
				contentPatchActionResult.Success = true;
				contentPatchActionResult.Message = "Patch generated and checked against the loaded asset.";
				contentPatchActionResult.Generated = contentPatchDefinition;
				contentPatchActionResult.Preview = new ContentPatchPreview
				{
					BeforeJson = (jToken2?.ToString(Formatting.Indented, Array.Empty<JsonConverter>()) ?? "(not present)"),
					AfterJson = (jToken3?.ToString(Formatting.Indented, Array.Empty<JsonConverter>()) ?? "(removed)"),
					PatchJson = new JArray(jObject2).ToString(Formatting.Indented, Array.Empty<JsonConverter>()),
					Note = "Preview uses the currently loaded asset. Other patch ordering and mod code can affect the result after restart." + ((jToken is JArray && contentPatchDefinition.Operation == "add") ? " Adding to an array inserts an entry and shifts later entries." : "")
				};
				return contentPatchActionResult;
			}
			catch (Exception ex)
			{
				return new ContentPatchActionResult
				{
					Success = false,
					Message = ex.Message
				};
			}
		}
	}

	public ContentPatchActionResult SaveGeneratedContentPatch(ContentPatchGenerateRequest request)
	{
		lock (sync)
		{
			request.Guided = true;
			ContentPatchActionResult contentPatchActionResult = GenerateGuidedContentPatch(request);
			if (!contentPatchActionResult.Success || contentPatchActionResult.Generated == null)
			{
				return contentPatchActionResult;
			}
			contentPatchActionResult.Generated.Enabled = request.Activate;
			return SaveContentPatch(contentPatchActionResult.Generated);
		}
	}

	private static string[] PointerParts(string path)
	{
		if (path.Length == 0)
		{
			return Array.Empty<string>();
		}
		if (!path.StartsWith('/'))
		{
			throw new InvalidOperationException("The property path must start with '/'.");
		}
		string[] source = path.Substring(1).Split('/');
		if (source.Any((string x) => Regex.IsMatch(x, "~(?![01])")))
		{
			throw new InvalidOperationException("Invalid JSON pointer escape.");
		}
		return source.Select((string x) => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
	}

	private static JToken? ResolvePointer(JToken root, string path)
	{
		JToken jToken = root;
		string[] array = PointerParts(path);
		foreach (string text in array)
		{
			if (jToken is JObject jObject)
			{
				jToken = jObject[text];
				continue;
			}
			if (jToken is JArray jArray && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0 && result < jArray.Count)
			{
				jToken = jArray[result];
				continue;
			}
			return null;
		}
		return jToken;
	}

	public CustomPatchService(ICoreServerAPI api)
	{
		this.api = api;
	}

	public void Start()
	{
		lock (sync)
		{
			store = SuiteConfigStorage.Load<CustomPatchStore>(api, "NewfiesCustomPatches.json") ?? new CustomPatchStore();
			store.Clamp();
			contentStore = SuiteConfigStorage.Load<ContentPatchStore>(api, "NewfiesContentPatches.json") ?? new ContentPatchStore();
			contentStore.Clamp();
			contentRestartRequired = false;
			current = this;
			ApplyAllUnsafe();
			SaveUnsafe();
		}
	}

	public CustomPatchSnapshot GetSnapshot(ServerHealthSnapshot? health = null)
	{
		lock (sync)
		{
			List<CustomPatchRow> patches = store.Patches.Select(ToRow).ToList();
			CustomPatchSnapshot customPatchSnapshot = new CustomPatchSnapshot();
			customPatchSnapshot.Available = true;
			customPatchSnapshot.HarmonyId = "newfies.serveressentials.custompatches";
			customPatchSnapshot.ConfigPath = SuiteConfigStorage.DisplayPath("NewfiesCustomPatches.json");
			customPatchSnapshot.Patches = patches;
			customPatchSnapshot.Suggestions = BuildSuggestions(health);
			customPatchSnapshot.SupportedModes = new string[3] { "Observe", "ThrottleVoid", "SkipVoid" };
			customPatchSnapshot.Note = "Patch Studio uses constrained declarative Harmony recipes. It does not compile or execute arbitrary C# from the WebPanel.";
			return customPatchSnapshot;
		}
	}

	public ContentPatchSnapshot GetContentSnapshot()
	{
		lock (sync)
		{
			ContentPatchSnapshot contentPatchSnapshot = new ContentPatchSnapshot();
			contentPatchSnapshot.Available = true;
			contentPatchSnapshot.ConfigPath = SuiteConfigStorage.DisplayPath("NewfiesContentPatches.json");
			contentPatchSnapshot.GeneratedModPath = GetGeneratedContentModPath();
			contentPatchSnapshot.RestartRequired = contentRestartRequired;
			contentPatchSnapshot.Patches = contentStore.Patches.Select((ContentPatchDefinition x) => x.Clone()).ToList();
			contentPatchSnapshot.SupportedOperations = new string[7] { "add", "addMerge", "addEach", "remove", "replace", "move", "copy" };
			contentPatchSnapshot.Note = "Content patches are generated as a managed Vintage Story content mod. Changes take effect after the next server restart because JSON patches are applied during asset loading.";
			return contentPatchSnapshot;
		}
	}

	public ContentPatchActionResult SaveContentPatch(ContentPatchDefinition incoming)
	{
		lock (sync)
		{
			incoming = (incoming ?? new ContentPatchDefinition()).Clone();
			incoming.Clamp();
			incoming.Operation = NormalizeContentOperation(incoming.Operation);
			incoming.Side = NormalizeSide(incoming.Side);
			string text = ValidateContentPatch(incoming);
			if (text != null)
			{
				return ContentFail(text);
			}
			if (string.IsNullOrWhiteSpace(incoming.Id))
			{
				incoming.Id = Guid.NewGuid().ToString("N");
			}
			int num = contentStore.Patches.FindIndex((ContentPatchDefinition x) => string.Equals(x.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
			if (num < 0 && contentStore.Patches.Count >= 200)
			{
				return ContentFail("The patch pack contains 200 definitions. Delete an unused definition before adding another.");
			}
			List<ContentPatchDefinition> previous = contentStore.Patches.Select((ContentPatchDefinition x) => x.Clone()).ToList();
			if (num >= 0)
			{
				contentStore.Patches[num] = incoming;
			}
			else
			{
				contentStore.Patches.Add(incoming);
			}
			string text2 = PersistContentChange(previous);
			if (text2 != null)
			{
				return ContentFail(text2);
			}
			contentRestartRequired = true;
			return ContentSuccess((num >= 0) ? "JSON/content patch updated. Restart required to apply asset patches." : "JSON/content patch created. Restart required to apply asset patches.");
		}
	}

	public ContentPatchActionResult DeleteContentPatch(string id)
	{
		lock (sync)
		{
			List<ContentPatchDefinition> previous = contentStore.Patches.Select((ContentPatchDefinition x) => x.Clone()).ToList();
			if (contentStore.Patches.RemoveAll((ContentPatchDefinition x) => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) == 0)
			{
				return ContentFail("Content patch was not found.");
			}
			string text = PersistContentChange(previous);
			if (text != null)
			{
				return ContentFail(text);
			}
			contentRestartRequired = true;
			return ContentSuccess("Content patch deleted. Restart required to unload the previous asset patch state.");
		}
	}

	public ContentPatchActionResult SetContentPatchEnabled(string id, bool enabled)
	{
		lock (sync)
		{
			ContentPatchDefinition contentPatchDefinition = contentStore.Patches.FirstOrDefault((ContentPatchDefinition x) => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
			if (contentPatchDefinition == null)
			{
				return ContentFail("Content patch was not found.");
			}
			List<ContentPatchDefinition> previous = contentStore.Patches.Select((ContentPatchDefinition x) => x.Clone()).ToList();
			contentPatchDefinition.Enabled = enabled;
			string text = PersistContentChange(previous);
			if (text != null)
			{
				return ContentFail(text);
			}
			contentRestartRequired = true;
			return ContentSuccess(enabled ? "Content patch enabled. Restart required to apply it." : "Content patch disabled. Restart required to remove its loaded effect.");
		}
	}

	public ContentPatchActionResult GenerateContentPatch(ContentPatchGenerateRequest request)
	{
		if (request == null)
		{
			request = new ContentPatchGenerateRequest();
		}
		if (request.Guided)
		{
			return GenerateGuidedContentPatch(request);
		}
		string text = (request.Preset ?? "generic").Trim().ToLowerInvariant();
		ContentPatchDefinition contentPatchDefinition = new ContentPatchDefinition
		{
			Id = Guid.NewGuid().ToString("N"),
			Name = (string.IsNullOrWhiteSpace(request.Name) ? "Generated content patch" : request.Name.Trim()),
			Enabled = false,
			File = (request.File ?? "").Trim(),
			Operation = NormalizeContentOperation(request.Operation),
			Path = (request.Path ?? "").Trim(),
			FromPath = (request.FromPath ?? "").Trim(),
			ValueJson = (string.IsNullOrWhiteSpace(request.ValueJson) ? "null" : request.ValueJson.Trim()),
			Side = NormalizeSide(request.Side),
			Notes = "Generated by Patch Studio. Review the asset file and JSON pointer before enabling.",
			DependsOnModId = (request.DependsOnModId ?? "").Trim(),
			InvertDependency = request.InvertDependency
		};
		string text2 = EscapeJsonPointerToken(request.IngredientKey ?? "A");
		switch (text)
		{
		case "recipe-output-quantity":
			contentPatchDefinition.Operation = "add";
			contentPatchDefinition.Path = "/output/quantity";
			contentPatchDefinition.ValueJson = Math.Max(1, request.Quantity).ToString(CultureInfo.InvariantCulture);
			break;
		case "recipe-ingredient-quantity":
			contentPatchDefinition.Operation = "add";
			contentPatchDefinition.Path = "/ingredients/" + text2 + "/quantity";
			contentPatchDefinition.ValueJson = Math.Max(1, request.Quantity).ToString(CultureInfo.InvariantCulture);
			break;
		case "recipe-ingredient-code":
			contentPatchDefinition.Operation = "add";
			contentPatchDefinition.Path = "/ingredients/" + text2 + "/code";
			contentPatchDefinition.ValueJson = System.Text.Json.JsonSerializer.Serialize(request.Code ?? "game:stick");
			break;
		case "recipe-enabled":
			contentPatchDefinition.Operation = "add";
			contentPatchDefinition.Path = "/enabled";
			contentPatchDefinition.ValueJson = (request.Enabled ? "true" : "false");
			break;
		}
		contentPatchDefinition.Clamp();
		string text3 = ValidateContentPatch(contentPatchDefinition);
		if (text3 != null && text != "generic")
		{
			return new ContentPatchActionResult
			{
				Success = false,
				Message = text3,
				Snapshot = GetContentSnapshot(),
				Generated = contentPatchDefinition
			};
		}
		return new ContentPatchActionResult
		{
			Success = true,
			Message = "Content patch template generated. Review it before saving/enabling.",
			Snapshot = GetContentSnapshot(),
			Generated = contentPatchDefinition
		};
	}

	private string? ValidateContentPatch(ContentPatchDefinition patch)
	{
		if (string.IsNullOrWhiteSpace(patch.Name))
		{
			return "Patch name is required.";
		}
		if (string.IsNullOrWhiteSpace(patch.File) || !patch.File.Contains(':'))
		{
			return "Asset file must include a domain, for example game:recipes/grid/tool/axe.json.";
		}
		if (!IsContentOperationSupported(patch.Operation))
		{
			return "Unsupported JSON patch operation.";
		}
		if (string.IsNullOrWhiteSpace(patch.Path) || !patch.Path.StartsWith('/'))
		{
			return "JSON patch path must start with '/'.";
		}
		if ((patch.Operation.Equals("move", StringComparison.OrdinalIgnoreCase) || patch.Operation.Equals("copy", StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrWhiteSpace(patch.FromPath) || !patch.FromPath.StartsWith('/')))
		{
			return "Move/copy patches require a fromPath beginning with '/'.";
		}
		if (OperationNeedsValue(patch.Operation))
		{
			try
			{
				using (JsonDocument.Parse(string.IsNullOrWhiteSpace(patch.ValueJson) ? "null" : patch.ValueJson))
				{
				}
			}
			catch (Exception ex)
			{
				return "Patch value is not valid JSON: " + ex.Message;
			}
		}
		return null;
	}

	private void RebuildContentPatchModUnsafe()
	{
		string generatedContentModPath = GetGeneratedContentModPath();
		string text = Path.Combine(generatedContentModPath, "assets", "newfiessuitecontentpatches", "patches");
		Directory.CreateDirectory(text);
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary["type"] = "content";
		dictionary["modid"] = "newfiessuitecontentpatches";
		dictionary["name"] = "Newfie's Suite Content Patches";
		dictionary["authors"] = new string[1] { "Newfie" };
		dictionary["description"] = "Generated JSON/recipe patches managed by Newfie's Server Suite Patch Studio.";
		dictionary["version"] = "1.0.0";
		dictionary["side"] = "Universal";
		dictionary["requiredOnServer"] = true;
		dictionary["requiredOnClient"] = contentStore.Patches.Any((ContentPatchDefinition x) => x.Enabled && x.Side != "server");
		dictionary["dependencies"] = new Dictionary<string, string> { ["game"] = "1.22.7" };
		Dictionary<string, object> value = dictionary;
		WriteContentAtomically(Path.Combine(generatedContentModPath, "modinfo.json"), System.Text.Json.JsonSerializer.Serialize(value, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
		List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
		foreach (ContentPatchDefinition item in contentStore.Patches.Where((ContentPatchDefinition x) => x.Enabled))
		{
			string text2 = ValidateContentPatch(item);
			if (text2 != null)
			{
				api.Logger.Warning("[ServerEssentials/PatchStudio] Content patch '{0}' skipped while generating pack: {1}", item.Name, text2);
				continue;
			}
			Dictionary<string, object> dictionary2 = new Dictionary<string, object>
			{
				["file"] = item.File,
				["op"] = NormalizeContentOperation(item.Operation),
				["path"] = item.Path
			};
			if (!string.IsNullOrWhiteSpace(item.Side))
			{
				dictionary2["side"] = item.Side;
			}
			if (!string.IsNullOrWhiteSpace(item.DependsOnModId))
			{
				Dictionary<string, object> dictionary3 = new Dictionary<string, object> { ["modid"] = item.DependsOnModId };
				if (item.InvertDependency)
				{
					dictionary3["invert"] = true;
				}
				dictionary2["dependsOn"] = new Dictionary<string, object>[1] { dictionary3 };
			}
			if (item.Operation.Equals("move", StringComparison.OrdinalIgnoreCase) || item.Operation.Equals("copy", StringComparison.OrdinalIgnoreCase))
			{
				dictionary2["fromPath"] = item.FromPath;
			}
			if (OperationNeedsValue(item.Operation))
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(item.ValueJson) ? "null" : item.ValueJson);
				dictionary2["value"] = jsonDocument.RootElement.Clone();
			}
			list.Add(dictionary2);
		}
		WriteContentAtomically(Path.Combine(text, "generated.json"), System.Text.Json.JsonSerializer.Serialize(list, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
	}

	private static void WriteContentAtomically(string path, string json)
	{
		string text = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				using StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), -1, leaveOpen: true);
				streamWriter.Write(json);
				streamWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			File.Move(text, path, overwrite: true);
		}
		finally
		{
			try
			{
				File.Delete(text);
			}
			catch
			{
			}
		}
	}

	private string GetGeneratedContentModPath()
	{
		return Path.Combine(api.GetOrCreateDataPath("Mods"), "NewfiesSuiteContentPatches");
	}

	private string? PersistContentChange(List<ContentPatchDefinition> previous)
	{
		try
		{
			RebuildContentPatchModUnsafe();
			SaveContentUnsafe();
			return null;
		}
		catch (Exception ex)
		{
			contentStore.Patches = previous;
			try
			{
				RebuildContentPatchModUnsafe();
				SaveContentUnsafe();
			}
			catch (Exception ex2)
			{
				api.Logger.Error("[PatchGenerator] Failed to restore patch files after a save error: {0}", ex2.Message);
			}
			return "Content patch could not be saved: " + ex.Message;
		}
	}

	private void SaveContentUnsafe()
	{
		SuiteConfigStorage.Store(api, contentStore, "NewfiesContentPatches.json");
	}

	private ContentPatchActionResult ContentSuccess(string message)
	{
		return new ContentPatchActionResult
		{
			Success = true,
			Message = message,
			Snapshot = GetContentSnapshot()
		};
	}

	private ContentPatchActionResult ContentFail(string message)
	{
		return new ContentPatchActionResult
		{
			Success = false,
			Message = message,
			Snapshot = GetContentSnapshot()
		};
	}

	private static bool OperationNeedsValue(string operation)
	{
		return operation.Equals("add", StringComparison.OrdinalIgnoreCase) || operation.Equals("addMerge", StringComparison.OrdinalIgnoreCase) || operation.Equals("addEach", StringComparison.OrdinalIgnoreCase) || operation.Equals("replace", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsContentOperationSupported(string? operation)
	{
		switch (NormalizeContentOperation(operation))
		{
		case "add":
		case "addMerge":
		case "addEach":
		case "remove":
		case "replace":
		case "move":
		case "copy":
			return true;
		default:
			return false;
		}
	}

	private static string NormalizeContentOperation(string? operation)
	{
		string text = (operation ?? "add").Trim();
		if (text.Equals("addmerge", StringComparison.OrdinalIgnoreCase))
		{
			return "addMerge";
		}
		if (text.Equals("addeach", StringComparison.OrdinalIgnoreCase))
		{
			return "addEach";
		}
		if (text.Equals("remove", StringComparison.OrdinalIgnoreCase))
		{
			return "remove";
		}
		if (text.Equals("replace", StringComparison.OrdinalIgnoreCase))
		{
			return "replace";
		}
		if (text.Equals("move", StringComparison.OrdinalIgnoreCase))
		{
			return "move";
		}
		if (text.Equals("copy", StringComparison.OrdinalIgnoreCase))
		{
			return "copy";
		}
		return "add";
	}

	private static string NormalizeSide(string? side)
	{
		string text = (side ?? "server").Trim().ToLowerInvariant();
		bool flag = ((text == "server" || text == "client") ? true : false);
		return flag ? text : "";
	}

	private static string EscapeJsonPointerToken(string value)
	{
		return (value ?? "").Replace("~", "~0").Replace("/", "~1");
	}

	public CustomPatchActionResult SaveFromRequest(CustomPatchDefinition incoming)
	{
		lock (sync)
		{
			if (incoming == null)
			{
				incoming = new CustomPatchDefinition();
			}
			incoming.Clamp();
			if (string.IsNullOrWhiteSpace(incoming.Name))
			{
				return Fail("Patch name is required.");
			}
			if (string.IsNullOrWhiteSpace(incoming.AssemblyName) || string.IsNullOrWhiteSpace(incoming.TypeName) || string.IsNullOrWhiteSpace(incoming.MethodName))
			{
				return Fail("Assembly, type and method are required.");
			}
			if (!IsModeSupported(incoming.Mode))
			{
				return Fail("Unsupported patch mode.");
			}
			if (string.IsNullOrWhiteSpace(incoming.Id))
			{
				incoming.Id = Guid.NewGuid().ToString("N");
			}
			int num = store.Patches.FindIndex((CustomPatchDefinition x) => string.Equals(x.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
			if (num >= 0)
			{
				store.Patches[num] = incoming;
			}
			else
			{
				store.Patches.Add(incoming);
			}
			if (store.Patches.Count > 100)
			{
				store.Patches = store.Patches.TakeLast(100).ToList();
			}
			ApplyAllUnsafe();
			SaveUnsafe();
			return Success((num >= 0) ? "Custom patch updated." : "Custom patch created.");
		}
	}

	public CustomPatchActionResult Delete(string id)
	{
		lock (sync)
		{
			if (store.Patches.RemoveAll((CustomPatchDefinition x) => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) == 0)
			{
				return Fail("Patch was not found.");
			}
			ApplyAllUnsafe();
			SaveUnsafe();
			return Success("Custom patch deleted.");
		}
	}

	public CustomPatchActionResult SetEnabled(string id, bool enabled)
	{
		lock (sync)
		{
			CustomPatchDefinition customPatchDefinition = store.Patches.FirstOrDefault((CustomPatchDefinition x) => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
			if (customPatchDefinition == null)
			{
				return Fail("Patch was not found.");
			}
			customPatchDefinition.Enabled = enabled;
			ApplyAllUnsafe();
			SaveUnsafe();
			return Success(enabled ? "Custom patch enabled." : "Custom patch disabled.");
		}
	}

	public CustomPatchActionResult Generate(CustomPatchGenerateRequest request)
	{
		if (request == null)
		{
			request = new CustomPatchGenerateRequest();
		}
		string text = (string.IsNullOrWhiteSpace(request.Mode) ? "Observe" : request.Mode.Trim());
		if (!IsModeSupported(text))
		{
			text = "Observe";
		}
		CustomPatchDefinition customPatchDefinition = new CustomPatchDefinition
		{
			Id = Guid.NewGuid().ToString("N"),
			Name = (string.IsNullOrWhiteSpace(request.Name) ? $"{request.TypeName}.{request.MethodName} {text}" : request.Name.Trim()),
			AssemblyName = (request.AssemblyName?.Trim() ?? ""),
			TypeName = (request.TypeName?.Trim() ?? ""),
			MethodName = (request.MethodName?.Trim() ?? ""),
			ParameterTypes = (request.ParameterTypes?.Where((string x) => !string.IsNullOrWhiteSpace(x)).Select((string x) => x.Trim()).Take(16)
				.ToList() ?? new List<string>()),
			Mode = text,
			ThrottleMilliseconds = Math.Clamp(request.ThrottleMilliseconds, 50, 60000),
			Enabled = false,
			Notes = "Generated by Patch Studio. Validate the exact target before enabling."
		};
		customPatchDefinition.Clamp();
		return new CustomPatchActionResult
		{
			Success = true,
			Message = "Patch template generated. Review it before saving/enabling.",
			Generated = customPatchDefinition
		};
	}

	private void ApplyAllUnsafe()
	{
		try
		{
			harmony.UnpatchAll("newfies.serveressentials.custompatches");
		}
		catch
		{
		}
		runtime.Clear();
		foreach (CustomPatchDefinition patch in store.Patches)
		{
			patch.RuntimeStatus = (patch.Enabled ? "Pending" : "Disabled");
			patch.RuntimeMessage = (patch.Enabled ? "Waiting for validation." : "Disabled by administrator.");
			if (!patch.Enabled)
			{
				continue;
			}
			try
			{
				MethodInfo methodInfo = ResolveMethod(patch);
				ValidateTarget(patch, methodInfo);
				if (runtime.ContainsKey(methodInfo))
				{
					throw new InvalidOperationException("Another enabled Patch Studio definition already targets this exact method. Merge or disable the duplicate first.");
				}
				RuntimePatchState value = new RuntimePatchState(patch.Id, patch.Mode, patch.ThrottleMilliseconds);
				runtime[methodInfo] = value;
				if (patch.Mode.Equals("Observe", StringComparison.OrdinalIgnoreCase))
				{
					harmony.Patch(methodInfo, new HarmonyMethod(typeof(CustomPatchService), "ObservePrefix"), new HarmonyMethod(typeof(CustomPatchService), "ObservePostfix"));
				}
				else if (patch.Mode.Equals("ThrottleVoid", StringComparison.OrdinalIgnoreCase))
				{
					harmony.Patch(methodInfo, new HarmonyMethod(typeof(CustomPatchService), "ThrottlePrefix"));
				}
				else if (patch.Mode.Equals("SkipVoid", StringComparison.OrdinalIgnoreCase))
				{
					harmony.Patch(methodInfo, new HarmonyMethod(typeof(CustomPatchService), "SkipVoidPrefix"));
				}
				patch.RuntimeStatus = "Applied";
				patch.RuntimeMessage = methodInfo.ToString() ?? methodInfo.Name;
			}
			catch (Exception ex)
			{
				patch.RuntimeStatus = "Error";
				patch.RuntimeMessage = ex.GetType().Name + ": " + ex.Message;
				api.Logger.Warning("[ServerEssentials/PatchStudio] Patch '{0}' was not applied: {1}", patch.Name, patch.RuntimeMessage);
			}
		}
	}

	private MethodInfo ResolveMethod(CustomPatchDefinition patch)
	{
		Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => string.Equals(a.GetName().Name, patch.AssemblyName, StringComparison.OrdinalIgnoreCase));
		if (assembly == null)
		{
			throw new InvalidOperationException("Assembly '" + patch.AssemblyName + "' is not loaded.");
		}
		Type type = assembly.GetType(patch.TypeName, throwOnError: false, ignoreCase: false) ?? throw new InvalidOperationException($"Type '{patch.TypeName}' was not found in {patch.AssemblyName}.");
		List<MethodInfo> list = (from x in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where string.Equals(x.Name, patch.MethodName, StringComparison.Ordinal)
			select x).ToList();
		if (patch.ParameterTypes.Count > 0)
		{
			list = list.Where((MethodInfo m) => ParametersMatch(m, patch.ParameterTypes)).ToList();
		}
		if (list.Count == 0)
		{
			throw new MissingMethodException(type.FullName, patch.MethodName);
		}
		if (list.Count > 1)
		{
			throw new AmbiguousMatchException("Multiple overloads match. Add parameter type names to the patch definition.");
		}
		return list[0];
	}

	private static bool ParametersMatch(MethodInfo method, IReadOnlyList<string> expected)
	{
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length != expected.Count)
		{
			return false;
		}
		for (int i = 0; i < parameters.Length; i++)
		{
			string a = parameters[i].ParameterType.FullName ?? parameters[i].ParameterType.Name;
			string name = parameters[i].ParameterType.Name;
			if (!string.Equals(a, expected[i], StringComparison.OrdinalIgnoreCase) && !string.Equals(name, expected[i], StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	private static void ValidateTarget(CustomPatchDefinition patch, MethodInfo method)
	{
		if (method.ContainsGenericParameters)
		{
			throw new InvalidOperationException("Open generic methods are not supported.");
		}
		string text = method.DeclaringType?.Assembly.GetName().Name ?? "";
		if (text.Equals("NewfiesServerEssentialsSuite", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Patch Studio cannot patch Server Suite itself. Use a normal Suite build for Suite changes.");
		}
		if (!patch.Mode.Equals("Observe", StringComparison.OrdinalIgnoreCase) && method.ReturnType != typeof(void))
		{
			throw new InvalidOperationException("ThrottleVoid and SkipVoid are limited to void-returning methods so skipped calls cannot fabricate return values.");
		}
	}

	private static void ObservePrefix(MethodBase __originalMethod, out long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	private static void ObservePostfix(MethodBase __originalMethod, long __state)
	{
		current?.RecordObserved(__originalMethod, __state);
	}

	private static bool ThrottlePrefix(MethodBase __originalMethod)
	{
		return current?.ShouldRun(__originalMethod) ?? true;
	}

	private static bool SkipVoidPrefix(MethodBase __originalMethod)
	{
		current?.RecordSkipped(__originalMethod);
		return false;
	}

	private void RecordObserved(MethodBase method, long started)
	{
		if (!runtime.TryGetValue(method, out RuntimePatchState value))
		{
			return;
		}
		long num = Math.Max(0L, Stopwatch.GetTimestamp() - started);
		Interlocked.Increment(ref value.Calls);
		Interlocked.Add(ref value.TotalElapsedTicks, num);
		long num2 = Volatile.Read(in value.MaxElapsedTicks);
		while (num > num2)
		{
			long num3 = Interlocked.CompareExchange(ref value.MaxElapsedTicks, num, num2);
			if (num3 == num2)
			{
				break;
			}
			num2 = num3;
		}
	}

	private bool ShouldRun(MethodBase method)
	{
		if (!runtime.TryGetValue(method, out RuntimePatchState value))
		{
			return true;
		}
		long timestamp = Stopwatch.GetTimestamp();
		long num = Math.Max(1L, (long)((double)value.ThrottleMilliseconds / 1000.0 * (double)Stopwatch.Frequency));
		long num2;
		do
		{
			num2 = Volatile.Read(in value.LastAllowedTicks);
			if (num2 != 0L && timestamp - num2 < num)
			{
				Interlocked.Increment(ref value.Skipped);
				return false;
			}
		}
		while (Interlocked.CompareExchange(ref value.LastAllowedTicks, timestamp, num2) != num2);
		Interlocked.Increment(ref value.Calls);
		return true;
	}

	private void RecordSkipped(MethodBase method)
	{
		if (runtime.TryGetValue(method, out RuntimePatchState value))
		{
			Interlocked.Increment(ref value.Skipped);
		}
	}

	private CustomPatchRow ToRow(CustomPatchDefinition patch)
	{
		RuntimePatchState value = runtime.FirstOrDefault((KeyValuePair<MethodBase, RuntimePatchState> x) => string.Equals(x.Value.Id, patch.Id, StringComparison.OrdinalIgnoreCase)).Value;
		long num = ((value == null) ? 0 : Volatile.Read(in value.Calls));
		long skipped = ((value == null) ? 0 : Volatile.Read(in value.Skipped));
		long num2 = ((value == null) ? 0 : Volatile.Read(in value.TotalElapsedTicks));
		long num3 = ((value == null) ? 0 : Volatile.Read(in value.MaxElapsedTicks));
		return new CustomPatchRow
		{
			Definition = patch.Clone(),
			Calls = num,
			Skipped = skipped,
			AverageMilliseconds = ((num <= 0 || num2 <= 0) ? 0.0 : ((double)num2 / (double)Stopwatch.Frequency * 1000.0 / (double)num)),
			MaxMilliseconds = ((num3 <= 0) ? 0.0 : ((double)num3 / (double)Stopwatch.Frequency * 1000.0))
		};
	}

	private List<CustomPatchSuggestion> BuildSuggestions(ServerHealthSnapshot? health)
	{
		List<CustomPatchSuggestion> list = new List<CustomPatchSuggestion>();
		if (health != null)
		{
			ServerProfileSummary lastProfile = health.LastProfile;
			if (lastProfile == null || lastProfile.CompletedUnixMilliseconds > 0)
			{
				foreach (ServerProfilerModHotspot item in (from x in health.ModHotspots
					where !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId)
					orderby x.TotalMs descending
					select x).Take(6))
				{
					list.Add(new CustomPatchSuggestion
					{
						Source = "Profiler",
						Title = "Inspect " + item.ModName,
						Detail = $"{item.PercentOfProfileTickTime:0.0}% of profiled tick time, max segment {item.MaxSingleMs:0.0} ms. Top marker: {item.TopMarker}",
						Recommendation = "Use Observe first. Only consider throttling a confirmed non-critical void method after measuring it.",
						ModId = item.ModId,
						Confidence = item.Confidence
					});
				}
				return list;
			}
		}
		return list;
	}

	private static bool IsModeSupported(string? mode)
	{
		return string.Equals(mode, "Observe", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "ThrottleVoid", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "SkipVoid", StringComparison.OrdinalIgnoreCase);
	}

	private void SaveUnsafe()
	{
		SuiteConfigStorage.Store(api, store, "NewfiesCustomPatches.json");
	}

	private CustomPatchActionResult Success(string message)
	{
		return new CustomPatchActionResult
		{
			Success = true,
			Message = message,
			Snapshot = GetSnapshot()
		};
	}

	private CustomPatchActionResult Fail(string message)
	{
		return new CustomPatchActionResult
		{
			Success = false,
			Message = message,
			Snapshot = GetSnapshot()
		};
	}

	public void Dispose()
	{
		lock (sync)
		{
			try
			{
				harmony.UnpatchAll("newfies.serveressentials.custompatches");
			}
			catch
			{
			}
			runtime.Clear();
			if (current == this)
			{
				current = null;
			}
		}
	}
}
