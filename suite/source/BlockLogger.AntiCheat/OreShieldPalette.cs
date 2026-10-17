using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldPalette
{
	private static readonly BlockPos SideProbePos = new BlockPos(0, 0, 0, 0);

	public bool[] Opaque { get; }

	public bool[] ProtectedOre { get; }

	public int[] HostByBlock { get; }

	private string?[] VeinFamilyByBlock { get; }

	public int ProtectedOreVariants { get; }

	public int HostVariants { get; }

	public int SkippedOreCandidates { get; }

	public IReadOnlyList<string> SkippedOreCodes { get; }

	public OreShieldPalette(IWorldAccessor world, AntiCheatConfig config)
	{
		int count = world.Blocks.Count;
		Opaque = new bool[count];
		ProtectedOre = new bool[count];
		HostByBlock = new int[count];
		VeinFamilyByBlock = new string[count];
		Dictionary<string, List<Block>> dictionary = new Dictionary<string, List<Block>>(StringComparer.OrdinalIgnoreCase);
		foreach (Block block2 in world.Blocks)
		{
			if (block2?.Code == null || block2.Id < 0 || block2.Id >= count)
			{
				continue;
			}
			bool flag = IsSafeOpaqueCube(block2, world.BlockAccessor);
			Opaque[block2.Id] = flag;
			if (!flag)
			{
				continue;
			}
			string rockVariant = GetRockVariant(block2);
			if (!string.IsNullOrWhiteSpace(rockVariant) && LooksLikeHostRock(block2))
			{
				if (!dictionary.TryGetValue(rockVariant, out var value))
				{
					value = (dictionary[rockVariant] = new List<Block>());
				}
				value.Add(block2);
			}
		}
		int num = 0;
		foreach (List<Block> value2 in dictionary.Values)
		{
			foreach (Block item in value2)
			{
				if (HostByBlock[item.Id] == 0)
				{
					HostByBlock[item.Id] = item.Id;
					num++;
				}
			}
		}
		int num2 = 0;
		int num3 = 0;
		List<string> list2 = new List<string>();
		foreach (Block block3 in world.Blocks)
		{
			if (block3?.Code == null || block3.Id <= 0 || block3.Id >= count)
			{
				continue;
			}
			string rockVariant2 = GetRockVariant(block3);
			if (string.IsNullOrWhiteSpace(rockVariant2) || !LooksLikeOre(block3, config))
			{
				continue;
			}
			if (!IsSafeOpaqueCube(block3, world.BlockAccessor))
			{
				num3++;
				if (list2.Count < 32)
				{
					list2.Add($"{block3.Code} (unsafe/non-cube)");
				}
				continue;
			}
			Block block = ResolveHost(block3, rockVariant2, dictionary);
			if (block == null || block.Id <= 0 || block.Id >= count || !IsVirtualizationCompatible(block3, block, world.BlockAccessor))
			{
				num3++;
				if (list2.Count < 32)
				{
					list2.Add($"{block3.Code} (no safe exact host)");
				}
			}
			else
			{
				HostByBlock[block.Id] = block.Id;
				HostByBlock[block3.Id] = block.Id;
				ProtectedOre[block3.Id] = true;
				VeinFamilyByBlock[block3.Id] = BuildVeinFamily(block3, rockVariant2);
				num2++;
			}
		}
		ProtectedOreVariants = num2;
		HostVariants = num;
		SkippedOreCandidates = num3;
		SkippedOreCodes = list2;
	}

	public bool IsOpaque(int blockId)
	{
		if (blockId < 0 || blockId >= Opaque.Length)
		{
			return true;
		}
		return Opaque[blockId];
	}

	public bool TryGetHost(int blockId, out int hostId)
	{
		hostId = ((blockId >= 0 && blockId < HostByBlock.Length) ? HostByBlock[blockId] : 0);
		return hostId != 0;
	}

	public bool SameVeinFamily(int firstBlockId, int secondBlockId)
	{
		if (firstBlockId <= 0 || secondBlockId <= 0 || firstBlockId >= ProtectedOre.Length || secondBlockId >= ProtectedOre.Length || !ProtectedOre[firstBlockId] || !ProtectedOre[secondBlockId])
		{
			return false;
		}
		string text = VeinFamilyByBlock[firstBlockId];
		string b = VeinFamilyByBlock[secondBlockId];
		return !string.IsNullOrWhiteSpace(text) && string.Equals(text, b, StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildVeinFamily(Block block, string rock)
	{
		string text = block.Code?.Domain ?? "game";
		string text2 = block.Code?.Path ?? string.Empty;
		string[] array = new string[3]
		{
			"-" + rock,
			"_" + rock,
			"/" + rock
		};
		foreach (string text3 in array)
		{
			if (text2.EndsWith(text3, StringComparison.OrdinalIgnoreCase))
			{
				string text4 = text2;
				int length = text3.Length;
				text2 = text4.Substring(0, text4.Length - length);
				break;
			}
		}
		string grade = block.Variant?["grade"];
		if (!string.IsNullOrWhiteSpace(grade))
		{
			text2 = string.Join('-', from part in text2.Split('-')
				where !part.Equals(grade, StringComparison.OrdinalIgnoreCase)
				select part);
		}
		return text + ":" + text2;
	}

	private static bool IsSafeOpaqueCube(Block block, IBlockAccessor blockAccessor)
	{
		try
		{
			if (!block.AllSidesOpaque || !AllSidesSolid(block) || block.DrawType != EnumDrawType.Cube)
			{
				return false;
			}
			if (!string.IsNullOrWhiteSpace(block.EntityClass) || block.ForFluidsLayer || block.PartialSelection || block.Climbable)
			{
				return false;
			}
			Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccessor, SideProbePos);
			Cuboidf[] selectionBoxes = block.GetSelectionBoxes(blockAccessor, SideProbePos);
			if (!IsUnitCube(collisionBoxes) || !IsUnitCube(selectionBoxes))
			{
				return false;
			}
			byte[] lightHsv = block.GetLightHsv(blockAccessor, SideProbePos);
			return lightHsv == null || lightHsv.Length < 3 || lightHsv[2] == 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool AllSidesSolid(Block block)
	{
		for (int i = 0; i < 6; i++)
		{
			if (!block.SideIsSolid(SideProbePos, i))
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsVirtualizationCompatible(Block ore, Block host, IBlockAccessor blockAccessor)
	{
		try
		{
			return ore.GetLightAbsorption(blockAccessor, SideProbePos) == host.GetLightAbsorption(blockAccessor, SideProbePos) && IsSafeOpaqueCube(host, blockAccessor);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsUnitCube(Cuboidf[]? boxes)
	{
		if (boxes == null || boxes.Length != 1 || boxes[0] == null)
		{
			return false;
		}
		Cuboidf cuboidf = boxes[0];
		return Math.Abs(cuboidf.X1) <= 0.0001f && Math.Abs(cuboidf.Y1) <= 0.0001f && Math.Abs(cuboidf.Z1) <= 0.0001f && Math.Abs(cuboidf.X2 - 1f) <= 0.0001f && Math.Abs(cuboidf.Y2 - 1f) <= 0.0001f && Math.Abs(cuboidf.Z2 - 1f) <= 0.0001f;
	}

	private static string? GetRockVariant(Block block)
	{
		if (block.Variant != null && block.Variant.TryGetValue("rock", out var value) && !string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		string text = block.Code?.Path ?? "";
		if (text.StartsWith("rock-", StringComparison.OrdinalIgnoreCase) && text.Length > 5)
		{
			return text.Substring(5);
		}
		return null;
	}

	private static bool LooksLikeHostRock(Block block)
	{
		string text = block.Code?.Path ?? "";
		return text.StartsWith("rock-", StringComparison.OrdinalIgnoreCase) || text.Equals("rock", StringComparison.OrdinalIgnoreCase);
	}

	private static bool LooksLikeOre(Block block, AntiCheatConfig config)
	{
		string haystack = block.Code?.ToString() ?? "";
		string text = block.Code?.Path ?? "";
		string text2 = text.ToLowerInvariant();
		foreach (string item in config.OreShieldExcludedBlockPatterns ?? new List<string>())
		{
			if (!string.IsNullOrWhiteSpace(item) && WildcardUtil.Match(item.Trim(), haystack))
			{
				return false;
			}
		}
		foreach (string item2 in config.OreShieldAdditionalOrePatterns ?? new List<string>())
		{
			if (!string.IsNullOrWhiteSpace(item2) && WildcardUtil.Match(item2.Trim(), haystack))
			{
				return true;
			}
		}
		if (block.BlockMaterial == EnumBlockMaterial.Ore)
		{
			return true;
		}
		if (text2.Equals("ore", StringComparison.Ordinal) || text2.StartsWith("ore-", StringComparison.Ordinal) || text2.EndsWith("-ore", StringComparison.Ordinal) || text2.Contains("-ore-", StringComparison.Ordinal) || text2.StartsWith("ore_", StringComparison.Ordinal) || text2.EndsWith("_ore", StringComparison.Ordinal) || text2.Contains("_ore_", StringComparison.Ordinal))
		{
			return true;
		}
		if (!config.OreShieldProtectModdedOres)
		{
			return false;
		}
		string[] source = text2.Split(new char[4] { '-', '_', '/', '.' }, StringSplitOptions.RemoveEmptyEntries);
		if (source.Any((string t) => t.Equals("ore", StringComparison.Ordinal) || t.Equals("mineral", StringComparison.Ordinal) || t.Equals("deposit", StringComparison.Ordinal)))
		{
			return true;
		}
		if (block.Variant != null)
		{
			foreach (KeyValuePair<string, string> item3 in block.Variant)
			{
				bool flag;
				switch (item3.Key?.Trim().ToLowerInvariant() ?? "")
				{
				case "ore":
				case "oretype":
				case "ore-type":
				case "metal":
				case "mineral":
				case "deposit":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					return true;
				}
			}
		}
		if (config.OreShieldStrictOreMatching)
		{
			return false;
		}
		return text2.Contains("ore", StringComparison.Ordinal) || text2.Contains("mineral", StringComparison.Ordinal) || text2.Contains("deposit", StringComparison.Ordinal);
	}

	private static Block? ResolveHost(Block ore, string rock, Dictionary<string, List<Block>> hostsByRock)
	{
		if (!hostsByRock.TryGetValue(rock, out List<Block> value) || value.Count == 0)
		{
			return null;
		}
		string domain = ore.Code?.Domain ?? "game";
		string expectedPath = "rock-" + rock;
		Block block = value.FirstOrDefault((Block x) => string.Equals(x.Code?.Domain, domain, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Code?.Path, expectedPath, StringComparison.OrdinalIgnoreCase));
		if (block != null)
		{
			return block;
		}
		return value.FirstOrDefault((Block x) => string.Equals(x.Code?.Domain, "game", StringComparison.OrdinalIgnoreCase) && string.Equals(x.Code?.Path, expectedPath, StringComparison.OrdinalIgnoreCase));
	}
}
