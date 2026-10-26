using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// Right-click tool items. The tool is chosen by the item's <c>sailingTool</c> attribute and handled on the server by
/// <see cref="SailingModSystem.UseTool"/>. Items with <c>consumable: true</c> use up one item when the tool reports success.
/// The spyglass zooms the client's view while held down.
/// </summary>
public sealed class ItemSailingTool : Item
{
    private string Tool => Attributes?["sailingTool"]?.AsString() ?? "";

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        handling = EnumHandHandling.PreventDefault;
        string tool = Tool;
        if (tool == "spyglass")
        {
            if (api is ICoreClientAPI capi) SpyglassZoom.Begin(capi, capi.ModLoader.GetModSystem<SailingModSystem>()?.SpyglassFraction ?? 0.22);
            return;
        }
        if (!firstEvent || api.Side != EnumAppSide.Server) return;
        if ((byEntity as EntityPlayer)?.Player is not IServerPlayer player) return;

        var sys = api.ModLoader.GetModSystem<SailingModSystem>();
        var (text, used) = tool == "chart" ? sys?.UseChart(player, slot) ?? ("", false) : sys?.UseTool(player, tool) ?? ("", false);
        if (text.Length > 0) player.SendMessage(GlobalConstants.InfoLogChatGroup, text, EnumChatType.Notification);
        if (!used) return;
        if (tool == "chart" || tool == "balloon" || (Attributes?["consumable"]?.AsBool() ?? false)) { slot.TakeOut(1); slot.MarkDirty(); }
        else if (Durability > 1) DamageItem(api.World, byEntity, slot, 1);
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        => Tool == "spyglass";

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
    {
        if (Tool == "spyglass" && api is ICoreClientAPI capi) SpyglassZoom.End(capi);
    }

    public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
    {
        if (Tool == "spyglass" && api is ICoreClientAPI capi) SpyglassZoom.End(capi);
        return true;
    }
}
