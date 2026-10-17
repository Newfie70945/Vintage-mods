using System;
using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

public sealed class SuitePanelElement : GuiElement
{
	private readonly string section;

	public SuitePanelElement(ICoreClientAPI capi, ElementBounds bounds, string section)
		: base(capi, bounds)
	{
		this.section = section ?? "";
	}

	public override void ComposeElements(Context ctx, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();
		double drawX = Bounds.drawX;
		double drawY = Bounds.drawY;
		double innerWidth = Bounds.InnerWidth;
		double innerHeight = Bounds.InnerHeight;
		ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.4);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(3.0), drawY + GuiElement.scaled(5.0), innerWidth, innerHeight, GuiElement.scaled(8.0));
		ctx.Fill();
		ctx.SetSourceRGBA(0.047, 0.063, 0.082, 0.985);
		GuiElement.RoundRectangle(ctx, drawX, drawY, innerWidth, innerHeight, GuiElement.scaled(8.0));
		ctx.Fill();
		ctx.SetSourceRGBA(0.086, 0.114, 0.145, 1.0);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(1.0), drawY + GuiElement.scaled(1.0), innerWidth - GuiElement.scaled(2.0), GuiElement.scaled(37.0), GuiElement.scaled(7.0));
		ctx.Fill();
		ctx.Save();
		ctx.Rectangle(drawX + GuiElement.scaled(1.0), drawY + GuiElement.scaled(2.0), innerWidth - GuiElement.scaled(2.0), GuiElement.scaled(34.0));
		ctx.Clip();
		ctx.LineWidth = GuiElement.scaled(0.5);
		ctx.SetSourceRGBA(0.65, 0.76, 0.85, 0.035);
		for (double num = 3.0; num < 36.0; num += 3.0)
		{
			ctx.MoveTo(drawX + GuiElement.scaled(1.0), drawY + GuiElement.scaled(num));
			ctx.LineTo(drawX + innerWidth - GuiElement.scaled(1.0), drawY + GuiElement.scaled(num));
		}
		ctx.Stroke();
		ctx.Restore();
		ctx.SetSourceRGBA(0.067, 0.09, 0.118, 0.98);
		ctx.Rectangle(drawX + GuiElement.scaled(16.0), drawY + GuiElement.scaled(38.0), innerWidth - GuiElement.scaled(32.0), GuiElement.scaled(40.0));
		ctx.Fill();
		ctx.SetSourceRGBA(0.055, 0.075, 0.098, 0.98);
		ctx.Rectangle(drawX + GuiElement.scaled(16.0), drawY + GuiElement.scaled(78.0), innerWidth - GuiElement.scaled(32.0), GuiElement.scaled(36.0));
		ctx.Fill();
		ctx.SetSourceRGBA(0.059, 0.082, 0.11, 0.99);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(16.0), drawY + GuiElement.scaled(119.0), innerWidth - GuiElement.scaled(32.0), Math.Max(GuiElement.scaled(8.0), innerHeight - GuiElement.scaled(135.0)), GuiElement.scaled(6.0));
		ctx.Fill();
		ctx.LineWidth = GuiElement.scaled(1.0);
		ctx.SetSourceRGBA(0.55, 0.67, 0.78, 0.12);
		ctx.MoveTo(drawX + GuiElement.scaled(18.0), drawY + GuiElement.scaled(77.5));
		ctx.LineTo(drawX + innerWidth - GuiElement.scaled(18.0), drawY + GuiElement.scaled(77.5));
		ctx.Stroke();
		ctx.MoveTo(drawX + GuiElement.scaled(18.0), drawY + GuiElement.scaled(114.5));
		ctx.LineTo(drawX + innerWidth - GuiElement.scaled(18.0), drawY + GuiElement.scaled(114.5));
		ctx.Stroke();
		ctx.LineWidth = GuiElement.scaled(1.0);
		ctx.SetSourceRGBA(0.306, 0.384, 0.463, 0.48);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(0.5), drawY + GuiElement.scaled(0.5), innerWidth - GuiElement.scaled(1.0), innerHeight - GuiElement.scaled(1.0), GuiElement.scaled(8.0));
		ctx.Stroke();
		ctx.SetSourceRGBA(0.306, 0.384, 0.463, 0.23);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(16.5), drawY + GuiElement.scaled(119.5), innerWidth - GuiElement.scaled(33.0), Math.Max(GuiElement.scaled(7.0), innerHeight - GuiElement.scaled(136.0)), GuiElement.scaled(6.0));
		ctx.Stroke();
		ctx.SetSourceRGBA(0.49, 0.714, 1.0, (section == "anticheat") ? 0.94 : 0.8);
		GuiElement.RoundRectangle(ctx, drawX + GuiElement.scaled(10.0), drawY + GuiElement.scaled(11.0), GuiElement.scaled(3.0), GuiElement.scaled(17.0), GuiElement.scaled(1.5));
		ctx.Fill();
	}
}
