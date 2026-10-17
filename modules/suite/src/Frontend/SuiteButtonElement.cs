using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

/// <summary>
/// Dark slate button backplate used by the Shift+L administration suite.
/// The actual click target remains Vintage Story's GuiElementTextButton in style None,
/// preserving native input/focus/sound behavior while avoiding the stock brown texture.
/// </summary>
internal sealed class SuiteButtonBackplateElement : GuiElement
{
    private readonly bool selected;

    public SuiteButtonBackplateElement(ICoreClientAPI capi, ElementBounds bounds, bool selected) : base(capi, bounds)
    {
        this.selected = selected;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double w = Bounds.InnerWidth;
        double h = Bounds.InnerHeight;

        // Flat controls keep dense settings pages quiet. A selected page has a
        // brighter surface and a short accent rule, so state does not rely on text.
        if (selected)
            ctx.SetSourceRGBA(0.118, 0.212, 0.310, 1.0);
        else
            ctx.SetSourceRGBA(0.082, 0.114, 0.149, 0.99);

        RoundRectangle(ctx, x, y, w, h, scaled(6));
        ctx.Fill();

        ctx.LineWidth = scaled(1);
        if (selected)
            ctx.SetSourceRGBA(0.49, 0.714, 1.0, 0.44);
        else
            ctx.SetSourceRGBA(0.337, 0.420, 0.502, 0.34);

        RoundRectangle(ctx, x + scaled(0.5), y + scaled(0.5), w - scaled(1), h - scaled(1), scaled(6));
        ctx.Stroke();

        if (selected)
        {
            ctx.SetSourceRGBA(0.49, 0.714, 1.0, 0.92);
            RoundRectangle(ctx, x + scaled(9), y + h - scaled(3), Math.Max(scaled(4), w - scaled(18)), scaled(2), scaled(1));
            ctx.Fill();
        }
    }
}

internal static class SuiteButtonComposerExtensions
{
    /// <summary>
    /// Fits localized button text without ever allowing CairoFont.AutoFontSize()
    /// to see an uncalculated/zero-width bounds object. A zero inner width makes
    /// Vintage Story's AutoFontSize calculate a negative font size, which Cairo
    /// renders mirrored/upside-down. Keep the fit box in fixed units, calculate
    /// its world bounds first, and restore the original size if measurement ever
    /// returns an invalid value.
    /// </summary>
    private static void SafeAutoFit(
        CairoFont font,
        string text,
        ElementBounds bounds,
        double horizontalInset,
        double verticalInset,
        double minimumWidth,
        double minimumHeight)
    {
        double originalSize = font.UnscaledFontsize;
        var fitBounds = bounds.FlatCopy();
        fitBounds.fixedWidth = Math.Max(minimumWidth, fitBounds.fixedWidth - horizontalInset);
        fitBounds.fixedHeight = Math.Max(minimumHeight, fitBounds.fixedHeight - verticalInset);

        // AutoFontSize uses ElementBounds.InnerWidth (calculated/absolute width),
        // not fixedWidth. Always calculate the copy before measuring the text.
        fitBounds.CalcWorldBounds();

        if (!string.IsNullOrEmpty(text) && fitBounds.InnerWidth > 1)
        {
            font.AutoFontSize(text, fitBounds, true);
        }

        if (font.UnscaledFontsize <= 0 || double.IsNaN(font.UnscaledFontsize) || double.IsInfinity(font.UnscaledFontsize))
        {
            font.UnscaledFontsize = originalSize;
        }
    }

    public static GuiComposer AddSuiteInset(this GuiComposer composer, ElementBounds bounds)
    {
        if (!composer.Composed) composer.AddStaticElement(new SuiteSettingsCardElement(composer.Api, bounds));
        return composer;
    }

    public static GuiComposer AddSuiteButton(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        bool selected = false,
        string? key = null)
    {
        if (!composer.Composed)
        {
            composer.AddStaticElement(new SuiteButtonBackplateElement(composer.Api, bounds.FlatCopy(), selected));
            var font = CairoFont.WhiteSmallText()
                .WithWeight(FontWeight.Bold)
                .WithOrientation(EnumTextOrientation.Center)
                .WithColor(selected
                    ? new[] { 0.94, 0.97, 1.0, 1.0 }
                    : new[] { 0.83, 0.88, 0.93, 1.0 });

            // Localized labels can be substantially longer than English.
            // Shrink the font only when required so translated text stays inside
            // the fixed Shift+L button without overlapping neighbouring controls.
            // Extra inset is deliberate: Cyrillic/German/French glyph runs can render
            // wider than English even when character counts look similar.
            SafeAutoFit(font, text, bounds, 20, 6, 24, 16);

            composer.AddButton(text, onClick, bounds, font, EnumButtonStyle.None, key);
        }
        return composer;
    }

    /// <summary>
    /// Lightweight blue action used inside scrolling result rows. Unlike the full
    /// suite button it has no static backplate, so moving its bounds during smooth
    /// scrolling cannot leave a baked background behind.
    /// </summary>
    public static GuiComposer AddSuiteInlineAction(
        this GuiComposer composer,
        string text,
        ActionConsumable onClick,
        ElementBounds bounds,
        string? key = null)
    {
        if (!composer.Composed)
        {
            var font = CairoFont.WhiteSmallText()
                .WithWeight(FontWeight.Bold)
                .WithOrientation(EnumTextOrientation.Center)
                .WithColor(new[] { 0.49, 0.714, 1.0, 1.0 });

            SafeAutoFit(font, text, bounds, 14, 4, 20, 14);

            composer.AddButton(text, onClick, bounds, font, EnumButtonStyle.None, key);
        }
        return composer;
    }
}

internal sealed class SuiteSettingsCardElement : GuiElement
{
    public SuiteSettingsCardElement(ICoreClientAPI capi, ElementBounds bounds) : base(capi, bounds) { }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double w = Bounds.InnerWidth;
        double h = Bounds.InnerHeight;

        if (h <= scaled(3))
        {
            ctx.SetSourceRGBA(0.45, 0.56, 0.67, 0.22);
            ctx.Rectangle(x, y, w, h); ctx.Fill();
            return;
        }
        ctx.SetSourceRGBA(0.071, 0.102, 0.137, 0.99);
        RoundRectangle(ctx, x, y, w, h, scaled(5));
        ctx.Fill();
        ctx.LineWidth = scaled(1);
        ctx.SetSourceRGBA(0.322, 0.400, 0.478, 0.38);
        RoundRectangle(ctx, x + scaled(0.5), y + scaled(0.5), w - scaled(1), h - scaled(1), scaled(5));
        ctx.Stroke();
    }
}
