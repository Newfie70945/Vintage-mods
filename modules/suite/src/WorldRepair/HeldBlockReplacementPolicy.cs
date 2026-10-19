namespace BlockLogger.WorldRepair;

internal static class HeldBlockReplacementPolicy
{
    internal static string Issue(int source,bool missing,bool heldBlock,int target,bool targetMissing,bool fluid,bool entity,bool retained)
    {
        if(source==0||!missing)return "Look at a missing (?) block. Healthy registered blocks and air are protected.";
        if(!heldBlock||target<=0||targetMissing)return "Hold a registered block in your active hotbar slot, not an item or an empty hand.";
        if(target==source)return "The held block is the same as the looked-at block.";
        if(fluid)return "This command supports solid blocks without fluid at the selected position.";
        if(entity||retained)return "This replacement requires block-entity/inventory data and is not the exact saved identity. Choose its original block or a compatible replacement; retained metadata will never be discarded or invented.";
        return "";
    }
}
