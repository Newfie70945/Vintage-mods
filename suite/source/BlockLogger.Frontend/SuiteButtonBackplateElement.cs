using System;
using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

internal sealed class SuiteButtonBackplateElement : GuiElement
{
	private readonly bool selected;

	public SuiteButtonBackplateElement(ICoreClientAPI capi, ElementBounds bounds, bool selected)
		: base(capi, bounds)
	{
		this.selected = selected;
	}

	public override void ComposeElements(Context ctx, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();
		double drawX = Bounds.drawX;
		double drawY = Bounds.drawY;
		double innerWidth = Bounds.InnerWidth;
		double innerHeight = Bounds.InnerHeight;
		if (selected)
		{
			ctx.SetSourceRGBA(0.118, 0.212, 0.31, 1.0);
		}
		else
		{
			ctx.SetSourceRGBA(0.082, 0.114, 0.149, 0.99);
		}
		GuiElement.RoundRectangle(ctx, drawX, drawY, innerWidth, innerHeight, GuiElement.scaled(6.0));
		ctx.Fill();
		ctx.LineWidth = GuiElement.scaled(1.0);
		if (selected)
		{
			ctx.SetSourceRGBA(0.49, 0.714, 1.0, 0.44);
		}
		else
		{
			ctx.SetSourceRGBA(0.337, 0.42, 0.502, 0.34);
		}
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(0.5), drawY + GuiElement.scaled(0.5), innerWidth - GuiElement.scaled(1.0), innerHeight - GuiElement.scaled(1.0), GuiElement.scaled(6.0));
		ctx.Stroke();
		if (selected)
		{
			ctx.SetSourceRGBA(0.49, 0.714, 1.0, 0.92);
			GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(9.0), drawY + innerHeight - GuiElement.scaled(3.0), Math.Max(GuiElement.scaled(4.0), innerWidth - GuiElement.scaled(18.0)), GuiElement.scaled(2.0), GuiElement.scaled(1.0));
			ctx.Fill();
		}
	}
}
