using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Resolves the server's block registry into the minimal information Ore Shield needs at packet time.
/// The hot path is array-only: no AssetLocation parsing, wildcard matching or dictionaries per block.
/// </summary>
internal sealed class OreShieldPalette
{
    private static readonly BlockPos SideProbePos = new(0, 0, 0, 0);

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
        VeinFamilyByBlock = new string?[count];

        var hostsByRock = new Dictionary<string, List<Block>>(StringComparer.OrdinalIgnoreCase);

        foreach (Block? block in world.Blocks)
        {
            if (block?.Code == null || block.IsMissing || block.Id < 0 || block.Id >= count) continue;

            bool opaque = IsSafeOpaqueCube(block, world.BlockAccessor);
            Opaque[block.Id] = opaque;
            if (!opaque) continue;

            string? rock = GetRockVariant(block);
            if (string.IsNullOrWhiteSpace(rock)) continue;

            if (LooksLikeHostRock(block))
            {
                if (!hostsByRock.TryGetValue(rock, out var list))
                    hostsByRock[rock] = list = new List<Block>();
                list.Add(block);
            }
        }

        int hostCount = 0;
        foreach (var candidates in hostsByRock.Values)
        {
            foreach (Block block in candidates)
            {
                if (HostByBlock[block.Id] == 0)
                {
                    HostByBlock[block.Id] = block.Id;
                    hostCount++;
                }
            }
        }

        int protectedCount = 0;
        int skipped = 0;
        var skippedCodes = new List<string>();

        foreach (Block? block in world.Blocks)
        {
            if (block?.Code == null || block.IsMissing || block.Id <= 0 || block.Id >= count) continue;

            string? rock = GetRockVariant(block);
            if (string.IsNullOrWhiteSpace(rock) || !LooksLikeOre(block, config)) continue;

            if (!IsSafeOpaqueCube(block, world.BlockAccessor))
            {
                skipped++;
                if (skippedCodes.Count < 32) skippedCodes.Add($"{block.Code} (unsafe/non-cube)");
                continue;
            }

            Block? host = ResolveHost(block, rock, hostsByRock);
            if (host == null || host.IsMissing || host.Id <= 0 || host.Id >= count || !IsVirtualizationCompatible(block, host, world.BlockAccessor))
            {
                skipped++;
                if (skippedCodes.Count < 32) skippedCodes.Add($"{block.Code} (no safe exact host)");
                continue;
            }

            HostByBlock[host.Id] = host.Id;
            HostByBlock[block.Id] = host.Id;
            ProtectedOre[block.Id] = true;
            VeinFamilyByBlock[block.Id] = BuildVeinFamily(block, rock);
            protectedCount++;

        }

        ProtectedOreVariants = protectedCount;
        HostVariants = hostCount;
        SkippedOreCandidates = skipped;
        SkippedOreCodes = skippedCodes;
    }

    public bool IsOpaque(int blockId)
    {
        // Unknown/unloaded data is fail-closed for Anti-Xray. A client must never receive a real
        // buried ore ID merely because an adjacent chunk is not loaded yet. Positive exposure is
        // handled separately by the authoritative reveal correction path.
        if (blockId < 0 || blockId >= Opaque.Length) return true;
        return Opaque[blockId];
    }

    public bool TryGetHost(int blockId, out int hostId)
    {
        hostId = blockId >= 0 && blockId < HostByBlock.Length ? HostByBlock[blockId] : 0;
        return hostId != 0;
    }

    public bool SameVeinFamily(int firstBlockId, int secondBlockId)
    {
        if (firstBlockId <= 0 || secondBlockId <= 0 ||
            firstBlockId >= ProtectedOre.Length || secondBlockId >= ProtectedOre.Length ||
            !ProtectedOre[firstBlockId] || !ProtectedOre[secondBlockId]) return false;
        string? a = VeinFamilyByBlock[firstBlockId];
        string? b = VeinFamilyByBlock[secondBlockId];
        return !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildVeinFamily(Block block, string rock)
    {
        string domain = block.Code?.Domain ?? "game";
        string path = block.Code?.Path ?? string.Empty;
        foreach (string suffix in new[] { "-" + rock, "_" + rock, "/" + rock })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }
        // Deposit grades belong to one mineral vein; a poor cell must connect to a rich
        // cell, including across a host-rock boundary. Keep the domain and mineral code.
        string? grade = block.Variant?["grade"];
        if (!string.IsNullOrWhiteSpace(grade))
            path = string.Join('-', path.Split('-').Where(part => !part.Equals(grade, StringComparison.OrdinalIgnoreCase)));
        return domain + ":" + path;
    }

    private static bool IsSafeOpaqueCube(Block block, IBlockAccessor blockAccessor)
    {
        if(block.IsMissing || block.Code==null)return false;
        try
        {
            // Do not bind Ore Shield to ABI-sensitive block fields when Vintage Story already
            // exposes a public query method. 1.22.x changed the runtime type/layout of several
            // collectible fields (for example LightHsv), so a direct field token compiled against
            // another API layout can throw MissingFieldException before normal safety handling.
            if (!block.AllSidesOpaque || !AllSidesSolid(block) || block.DrawType != EnumDrawType.Cube) return false;
            if (!string.IsNullOrWhiteSpace(block.EntityClass) || block.ForFluidsLayer || block.PartialSelection || block.Climbable) return false;

            Cuboidf[]? collisionBoxes = block.GetCollisionBoxes(blockAccessor, SideProbePos);
            Cuboidf[]? selectionBoxes = block.GetSelectionBoxes(blockAccessor, SideProbePos);
            if (!IsUnitCube(collisionBoxes) || !IsUnitCube(selectionBoxes)) return false;

            byte[]? lightHsv = block.GetLightHsv(blockAccessor, SideProbePos, null);
            return lightHsv == null || lightHsv.Length < 3 || lightHsv[2] == 0;
        }
        catch
        {
            // A modded block with position-sensitive/unsafe query methods is simply excluded from
            // Ore Shield virtualization; never let one unusual block disable the whole module.
            return false;
        }
    }

    private static bool AllSidesSolid(Block block)
    {
        // SideIsSolid(BlockPos,int) is the stable public API wrapper around the block's
        // side-solid representation. The probe position is only used by unusual overrides;
        // standard rock/ore blocks simply return their per-face solid state.
        for (int face = 0; face < 6; face++)
        {
            if (!block.SideIsSolid(SideProbePos, face)) return false;
        }
        return true;
    }

    private static bool IsVirtualizationCompatible(Block ore, Block host, IBlockAccessor blockAccessor)
    {
        // The client receives the host ID but the server-generated light arrays still came from the
        // authoritative chunk. Query light absorption through the public virtual API rather than
        // binding to the backing field, for the same ABI-safety reason as GetLightHsv above.
        try
        {
            return ore.GetLightAbsorption(blockAccessor, SideProbePos) == host.GetLightAbsorption(blockAccessor, SideProbePos) &&
                   IsSafeOpaqueCube(host, blockAccessor);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnitCube(Cuboidf[]? boxes)
    {
        if (boxes == null || boxes.Length != 1 || boxes[0] == null) return false;
        Cuboidf b = boxes[0];
        const float epsilon = 0.0001f;
        return Math.Abs(b.X1) <= epsilon && Math.Abs(b.Y1) <= epsilon && Math.Abs(b.Z1) <= epsilon &&
               Math.Abs(b.X2 - 1f) <= epsilon && Math.Abs(b.Y2 - 1f) <= epsilon && Math.Abs(b.Z2 - 1f) <= epsilon;
    }

    private static string? GetRockVariant(Block block)
    {
        if (block.Variant != null && block.Variant.TryGetValue("rock", out string? rock) && !string.IsNullOrWhiteSpace(rock))
            return rock.Trim();

        string path = block.Code?.Path ?? "";
        if (path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase) && path.Length > 5)
            return path[5..];

        return null;
    }

    private static bool LooksLikeHostRock(Block block)
    {
        string path = block.Code?.Path ?? "";
        // Host substitution must be semantically boring: use only conventional full-cube rock
        // blocks. Guessing a modded decorative/functional block as the host would create client
        // collision or interaction mismatches, so unknown host schemes fail open instead.
        return path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("rock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeOre(Block block, AntiCheatConfig config)
    {
        string code = block.Code?.ToString() ?? "";
        string path = block.Code?.Path ?? "";
        string lower = path.ToLowerInvariant();

        foreach (string pattern in config.OreShieldExcludedBlockPatterns ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(pattern) && WildcardUtil.Match(pattern.Trim(), code)) return false;
        }

        foreach (string pattern in config.OreShieldAdditionalOrePatterns ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(pattern) && WildcardUtil.Match(pattern.Trim(), code)) return true;
        }

        // Material is the strongest signal and works for well-behaved vanilla/modded ores without
        // relying on naming conventions.
        if (block.BlockMaterial == EnumBlockMaterial.Ore) return true;

        // Vanilla/common naming conventions: require ore to be a complete token, not merely the
        // letters "ore" somewhere in a word (for example "core-*" must never be classified as ore).
        if (lower.Equals("ore", StringComparison.Ordinal) ||
            lower.StartsWith("ore-", StringComparison.Ordinal) ||
            lower.EndsWith("-ore", StringComparison.Ordinal) ||
            lower.Contains("-ore-", StringComparison.Ordinal) ||
            lower.StartsWith("ore_", StringComparison.Ordinal) ||
            lower.EndsWith("_ore", StringComparison.Ordinal) ||
            lower.Contains("_ore_", StringComparison.Ordinal))
            return true;

        if (!config.OreShieldProtectModdedOres) return false;

        // In strict mode, unusual modded deposits must either declare Ore material, use an exact
        // ore/mineral/deposit token, or be explicitly listed by an administrator.
        string[] tokens = lower.Split(new[] { '-', '_', '/', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(t => t.Equals("ore", StringComparison.Ordinal) ||
                            t.Equals("mineral", StringComparison.Ordinal) ||
                            t.Equals("deposit", StringComparison.Ordinal)))
            return true;

        if (block.Variant != null)
        {
            foreach (var pair in block.Variant)
            {
                string key = pair.Key?.Trim().ToLowerInvariant() ?? "";
                if (key is "ore" or "oretype" or "ore-type" or "metal" or "mineral" or "deposit")
                    return true;
            }
        }

        if (config.OreShieldStrictOreMatching) return false;

        // Legacy compatibility mode for unusual older mods. Kept behind an explicit switch because
        // substring matching can produce false positives and should not be the production default.
        return lower.Contains("ore", StringComparison.Ordinal) ||
               lower.Contains("mineral", StringComparison.Ordinal) ||
               lower.Contains("deposit", StringComparison.Ordinal);
    }

    private static Block? ResolveHost(Block ore, string rock, Dictionary<string, List<Block>> hostsByRock)
    {
        if (!hostsByRock.TryGetValue(rock, out var candidates) || candidates.Count == 0) return null;

        string domain = ore.Code?.Domain ?? "game";
        string expectedPath = "rock-" + rock;

        // Do not choose an arbitrary decorative/functional rock block just because it shares the
        // same `rock` variant. Exact host paths prevent legitimate ores from being virtualized as
        // the wrong visible block. Prefer the ore's own domain, then vanilla game rock.
        Block? exactSameDomain = candidates.FirstOrDefault(x =>
            string.Equals(x.Code?.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Code?.Path, expectedPath, StringComparison.OrdinalIgnoreCase));
        if (exactSameDomain != null) return exactSameDomain;

        Block? exactVanilla = candidates.FirstOrDefault(x =>
            string.Equals(x.Code?.Domain, "game", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Code?.Path, expectedPath, StringComparison.OrdinalIgnoreCase));
        return exactVanilla;
    }
}
