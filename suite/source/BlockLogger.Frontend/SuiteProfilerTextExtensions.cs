using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

internal static class SuiteProfilerTextExtensions
{
	public static GuiComposer AddProfilerText(this GuiComposer composer, string text, CairoFont font, ElementBounds bounds)
	{
		if (!composer.Composed)
		{
			composer.AddStaticElement(new SuiteProfilerTextElement(composer.Api, text, font, bounds));
			composer.AddHoverText(WebUtility.HtmlEncode(text), CairoFont.WhiteSmallText(), 640, bounds.FlatCopy());
		}
		return composer;
	}

	public static GuiComposer AddProfilerRichText(this GuiComposer composer, string text, CairoFont font, ElementBounds bounds, string? key = null)
	{
		if (text.Contains("<strong>", StringComparison.OrdinalIgnoreCase))
		{
			font.WithWeight(FontWeight.Bold);
		}
		Match match = Regex.Match(text, "<font color=\"#([0-9a-fA-F]{6})\"");
		if (match.Success)
		{
			int num = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			font.WithColor(new double[4]
			{
				(double)((num >> 16) & 0xFF) / 255.0,
				(double)((num >> 8) & 0xFF) / 255.0,
				(double)(num & 0xFF) / 255.0,
				1.0
			});
		}
		string text2 = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", ""));
		return composer.AddProfilerText(text2, font, bounds);
	}
}
