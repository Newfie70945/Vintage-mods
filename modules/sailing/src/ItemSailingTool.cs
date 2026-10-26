using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// Right-click tool items (weather glass, wind vane, anchor, logbook). The tool is chosen by the item's
/// <c>sailingTool</c> attribute and handled on the server by <see cref="SailingModSystem.UseTool"/>.
/// </summary>
public sealed class ItemSailingTool : Item
{
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        handling = EnumHandHandling.PreventDefault;
        if (!firstEvent || api.Side != EnumAppSide.Server) return;
        if ((byEntity as EntityPlayer)?.Player is not IServerPlayer player) return;

        string tool = Attributes?["sailingTool"]?.AsString() ?? "";
        string text = api.ModLoader.GetModSystem<SailingModSystem>()?.UseTool(player, tool) ?? "";
        if (text.Length > 0) player.SendMessage(GlobalConstants.InfoLogChatGroup, text, EnumChatType.Notification);
    }
}
