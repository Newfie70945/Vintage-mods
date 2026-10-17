using System;
using Cairo;
using Vintagestory.API.Client;

namespace NewfiesCommunityMOTD;

internal sealed class MotdLogoTextComponent : RichTextComponentBase
{
	private readonly string imagePath;

	private readonly int width;

	private readonly int height;

	private readonly int gap;

	private readonly bool preserveAspect;

	public MotdLogoTextComponent(ICoreClientAPI api, string imagePath, int width, int height, int gap, bool preserveAspect)
		: base(api)
	{
		this.imagePath = imagePath;
		this.width = Math.Max(1, Math.Min(400, width));
		this.height = Math.Max(1, Math.Min(400, height));
		this.gap = Math.Max(0, gap);
		this.preserveAspect = preserveAspect;
		Float = EnumFloat.None;
		BoundsPerLine = Array.Empty<LineRectangled>();
	}

	public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
	{
		TextFlowPath textFlowPath = GetCurrentFlowPathSection(flowPath, lineY) ?? ((flowPath.Length != 0) ? flowPath[0] : new TextFlowPath(GuiElement.scaled(width)));
		double num = Math.Max(GuiElement.scaled(width), textFlowPath.X2 - textFlowPath.X1);
		double num2 = GuiElement.scaled(height + gap);
		BoundsPerLine = new LineRectangled[1]
		{
			new LineRectangled(textFlowPath.X1, lineY, num, num2)
		};
		nextOffsetX = 0.0;
		return EnumCalcBoundsResult.Nextline;
	}

	public override void ComposeElements(Context ctx, ImageSurface surface)
	{
		if (BoundsPerLine == null || BoundsPerLine.Length == 0)
		{
			return;
		}
		try
		{
			using ImageSurface imageSurface = new ImageSurface(imagePath);
			LineRectangled lineRectangled = BoundsPerLine[0];
			double num = Math.Min(lineRectangled.Width, GuiElement.scaled(width));
			double num2 = GuiElement.scaled(height);
			double num3 = num / (double)Math.Max(1, imageSurface.Width);
			double num4 = num2 / (double)Math.Max(1, imageSurface.Height);
			double num5 = num3;
			double num6 = num4;
			if (preserveAspect)
			{
				double num7 = Math.Min(num3, num4);
				num5 = (num6 = num7);
			}
			double num8 = (double)imageSurface.Width * num5;
			double num9 = (double)imageSurface.Height * num6;
			double tx = lineRectangled.X + (lineRectangled.Width - num8) / 2.0;
			double ty = lineRectangled.Y + (num2 - num9) / 2.0;
			ctx.Save();
			ctx.Translate(tx, ty);
			ctx.Scale(num5, num6);
			ctx.SetSourceSurface(imageSurface, 0, 0);
			ctx.Paint();
			ctx.Restore();
		}
		catch
		{
		}
	}
}
