using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

internal static class SuiteButtonComposerExtensions
{
	private static void SafeAutoFit(CairoFont font, string text, ElementBounds bounds, double horizontalInset, double verticalInset, double minimumWidth, double minimumHeight)
	{
		double unscaledFontsize = font.UnscaledFontsize;
		ElementBounds elementBounds = bounds.FlatCopy();
		elementBounds.fixedWidth = Math.Max(minimumWidth, elementBounds.fixedWidth - horizontalInset);
		elementBounds.fixedHeight = Math.Max(minimumHeight, elementBounds.fixedHeight - verticalInset);
		elementBounds.CalcWorldBounds();
		if (!string.IsNullOrEmpty(text) && elementBounds.InnerWidth > 1.0)
		{
			font.AutoFontSize(text, elementBounds);
		}
		if (font.UnscaledFontsize <= 0.0 || double.IsNaN(font.UnscaledFontsize) || double.IsInfinity(font.UnscaledFontsize))
		{
			font.UnscaledFontsize = unscaledFontsize;
		}
	}

	public static GuiComposer AddSuiteInset(this GuiComposer composer, ElementBounds bounds)
	{
		if (!composer.Composed)
		{
			composer.AddStaticElement(new SuiteSettingsCardElement(composer.Api, bounds));
		}
		return composer;
	}

	public static GuiComposer AddSuiteButton(this GuiComposer composer, string text, ActionConsumable onClick, ElementBounds bounds, bool selected = false, string? key = null)
	{
		if (!composer.Composed)
		{
			composer.AddStaticElement(new SuiteButtonBackplateElement(composer.Api, bounds.FlatCopy(), selected));
			CairoFont cairoFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Center)
				.WithColor((!selected) ? new double[4] { 0.83, 0.88, 0.93, 1.0 } : new double[4] { 0.94, 0.97, 1.0, 1.0 });
			SafeAutoFit(cairoFont, text, bounds, 20.0, 6.0, 24.0, 16.0);
			composer.AddButton(text, onClick, bounds, cairoFont, EnumButtonStyle.None, key);
		}
		return composer;
	}

	public static GuiComposer AddSuiteInlineAction(this GuiComposer composer, string text, ActionConsumable onClick, ElementBounds bounds, string? key = null)
	{
		if (!composer.Composed)
		{
			CairoFont cairoFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Center)
				.WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 });
			SafeAutoFit(cairoFont, text, bounds, 14.0, 4.0, 20.0, 14.0);
			composer.AddButton(text, onClick, bounds, cairoFont, EnumButtonStyle.None, key);
		}
		return composer;
	}
}
