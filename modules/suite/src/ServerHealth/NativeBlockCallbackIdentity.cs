namespace BlockLogger.ServerHealth;

// Exact native root markers only. A child path may belong to a mod; do not
// override its attribution merely because it ran beneath a native callback.
internal static class NativeBlockCallbackIdentity
{
    public static bool IsSurvivalRoot(string path) => path is
        "gmlbVintagestory.GameContent.BlockEntityFirepit" or
        "gmlbVintagestory.GameContent.BlockEntityBarrel" or
        "gmlbVintagestory.GameContent.BlockEntityWildFruitingBush";
}
