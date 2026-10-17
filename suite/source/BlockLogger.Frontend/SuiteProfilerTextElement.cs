using System;
using System.Globalization;
using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

internal sealed class SuiteProfilerTextElement : GuiElementStaticText
{
	private readonly string fullText;

	private readonly bool singleLine;

	public SuiteProfilerTextElement(ICoreClientAPI api, string text, CairoFont font, ElementBounds bounds)
		: base(api, text, EnumTextOrientation.Left, bounds, font)
	{
		fullText = text;
		singleLine = bounds.fixedHeight <= 30.0;
	}

	public override void ComposeTextElements(Context ctx, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();
		double absInnerHeight = Bounds.absInnerHeight;
		ctx.Save();
		try
		{
			double num = (offsetY = (offsetX = GuiElement.scaled(1.0)));
			ctx.NewPath();
			ctx.Rectangle(Bounds.drawX + num, Bounds.drawY + num, Math.Max(0.0, Bounds.InnerWidth - 2.0 * num), Math.Max(0.0, absInnerHeight - 2.0 * num));
			ctx.Clip();
			Font.SetupContext(ctx);
			Text = (singleLine ? FitLine(ctx, fullText, Math.Max(0.0, Bounds.InnerWidth - GuiElement.scaled(2.0))) : fullText);
			base.ComposeTextElements(ctx, surface);
		}
		finally
		{
			Bounds.absInnerHeight = absInnerHeight;
			ctx.Restore();
		}
	}

	internal static string FitUiLine(string text, double unscaledWidth)
	{
		using ImageSurface surface = new ImageSurface(Format.Argb32, 1, 1);
		using Context ctx = new Context(surface);
		CairoFont.WhiteSmallText().SetupContext(ctx);
		return FitLine(ctx, text, GuiElement.scaled(Math.Max(0.0, unscaledWidth - 2.0)));
	}

	internal static string FitLine(Context ctx, string text, double width)
	{
		text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
		if (Fits(text))
		{
			return text;
		}
		if (!Fits("…"))
		{
			return "";
		}
		int[] array = StringInfo.ParseCombiningCharacters(text);
		int num = 0;
		int num2 = array.Length;
		while (num < num2)
		{
			int num3 = (num + num2 + 1) / 2;
			if (Fits(text[..((num3 == array.Length) ? text.Length : array[num3])] + "…"))
			{
				num = num3;
			}
			else
			{
				num2 = num3 - 1;
			}
		}
		return text.Substring(0, (num == array.Length) ? text.Length : array[num]) + "…";
		bool Fits(string value)
		{
			TextExtents textExtents = ctx.TextExtents(value);
			return Math.Max(textExtents.XAdvance, textExtents.XBearing + textExtents.Width) <= width;
		}
	}
}
