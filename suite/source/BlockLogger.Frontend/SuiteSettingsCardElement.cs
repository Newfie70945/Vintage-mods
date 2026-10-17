using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

internal sealed class SuiteSettingsCardElement : GuiElement
{
	public SuiteSettingsCardElement(ICoreClientAPI capi, ElementBounds bounds)
		: base(capi, bounds)
	{
	}

	public override void ComposeElements(Context ctx, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();
		double drawX = Bounds.drawX;
		double drawY = Bounds.drawY;
		double innerWidth = Bounds.InnerWidth;
		double innerHeight = Bounds.InnerHeight;
		if (innerHeight <= GuiElement.scaled(3.0))
		{
			ctx.SetSourceRGBA(0.45, 0.56, 0.67, 0.22);
			ctx.Rectangle(drawX, drawY, innerWidth, innerHeight);
			ctx.Fill();
			return;
		}
		ctx.SetSourceRGBA(0.071, 0.102, 0.137, 0.99);
		GuiElement.RoundRectangle(ctx, drawX, drawY, innerWidth, innerHeight, GuiElement.scaled(5.0));
		ctx.Fill();
		ctx.LineWidth = GuiElement.scaled(1.0);
		ctx.SetSourceRGBA(0.322, 0.4, 0.478, 0.38);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(0.5), drawY + GuiElement.scaled(0.5), innerWidth - GuiElement.scaled(1.0), innerHeight - GuiElement.scaled(1.0), GuiElement.scaled(5.0));
		ctx.Stroke();
	}
}
