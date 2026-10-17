using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

/// <summary>Fixed profiler cells: native static text otherwise expands its height while wrapping.</summary>
internal sealed class SuiteProfilerTextElement : GuiElementStaticText
{
    private readonly string fullText;
    private readonly bool singleLine;

    public SuiteProfilerTextElement(ICoreClientAPI api, string text, CairoFont font, ElementBounds bounds)
        : base(api, text, EnumTextOrientation.Left, bounds, font)
    {
        fullText = text;
        singleLine = bounds.fixedHeight <= 30;
    }

    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double height = Bounds.absInnerHeight;
        ctx.Save();
        try
        {
            double inset = scaled(1);
            offsetX = inset;
            offsetY = inset;
            ctx.NewPath();
            ctx.Rectangle(Bounds.drawX + inset, Bounds.drawY + inset,
                Math.Max(0, Bounds.InnerWidth - 2 * inset), Math.Max(0, height - 2 * inset));
            ctx.Clip();
            Font.SetupContext(ctx);
            Text = singleLine ? FitLine(ctx, fullText, Math.Max(0, Bounds.InnerWidth - scaled(2))) : fullText;
            base.ComposeTextElements(ctx, surface);
        }
        finally
        {
            // The native renderer changes absInnerHeight to match wrapped text. Keep the
            // declared row geometry for hit testing and subsequent compositions.
            Bounds.absInnerHeight = height;
            ctx.Restore();
        }
    }

    internal static string FitUiLine(string text, double unscaledWidth)
    {
        using var surface = new ImageSurface(Format.Argb32, 1, 1);
        using var ctx = new Context(surface);
        CairoFont.WhiteSmallText().SetupContext(ctx);
        return FitLine(ctx, text, scaled(Math.Max(0, unscaledWidth - 2)));
    }

    internal static string FitLine(Context ctx, string text, double width)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        bool Fits(string value) { var ext = ctx.TextExtents(value); return Math.Max(ext.XAdvance, ext.XBearing + ext.Width) <= width; }
        if (Fits(text)) return text;
        const string suffix = "…";
        if (!Fits(suffix)) return "";
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int low = 0, high = starts.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            int end = middle == starts.Length ? text.Length : starts[middle];
            if (Fits(text[..end] + suffix)) low = middle; else high = middle - 1;
        }
        return text[..(low == starts.Length ? text.Length : starts[low])] + suffix;
    }
}

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
        // These table labels use only strong/font markup; encode/decode keeps raw profiler
        // values literal while retaining the emphasis and severity/confidence colors.
        if (text.Contains("<strong>", StringComparison.OrdinalIgnoreCase)) font.WithWeight(FontWeight.Bold);
        var color = Regex.Match(text, "<font color=\"#([0-9a-fA-F]{6})\"");
        if (color.Success)
        {
            int rgb = int.Parse(color.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            font.WithColor(new[] { ((rgb >> 16) & 255) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d, 1d });
        }
        string plain = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", ""));
        return composer.AddProfilerText(plain, font, bounds);
    }
}
