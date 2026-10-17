using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

/// <summary>
/// Dark, non-blurred shell for the Shift+L Server Essentials window.
/// Neutral slate surfaces and a fine header grain group navigation and content. The blue accent stays consistent across every administration section.
/// </summary>
public sealed class SuitePanelElement : GuiElement
{
    private readonly string section;

    public SuitePanelElement(ICoreClientAPI capi, ElementBounds bounds, string section) : base(capi, bounds)
    {
        this.section = section ?? "";
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double w = Bounds.InnerWidth;
        double h = Bounds.InnerHeight;

        // Shadow.
        ctx.SetSourceRGBA(0, 0, 0, 0.40);
        RoundRectangle(ctx, x + scaled(3), y + scaled(5), w, h, scaled(8));
        ctx.Fill();

        ctx.SetSourceRGBA(0.047, 0.063, 0.082, 0.985);
        RoundRectangle(ctx, x, y, w, h, scaled(8));
        ctx.Fill();

        // Header band.
        ctx.SetSourceRGBA(0.086, 0.114, 0.145, 1.0);
        RoundRectangle(ctx, x + scaled(1), y + scaled(1), w - scaled(2), scaled(37), scaled(7));
        ctx.Fill();

        // Fine static grain composed into the existing panel texture; no extra GPU resources.
        ctx.Save();
        ctx.Rectangle(x + scaled(1), y + scaled(2), w - scaled(2), scaled(34));
        ctx.Clip();
        ctx.LineWidth = scaled(0.5);
        ctx.SetSourceRGBA(0.65, 0.76, 0.85, 0.035);
        for (double row = 3; row < 36; row += 3)
        {
            ctx.MoveTo(x + scaled(1), y + scaled(row));
            ctx.LineTo(x + w - scaled(1), y + scaled(row));
        }
        ctx.Stroke();
        ctx.Restore();

        // Navigation bands.
        ctx.SetSourceRGBA(0.067, 0.090, 0.118, 0.98);
        ctx.Rectangle(x + scaled(16), y + scaled(38), w - scaled(32), scaled(40));
        ctx.Fill();
        ctx.SetSourceRGBA(0.055, 0.075, 0.098, 0.98);
        ctx.Rectangle(x + scaled(16), y + scaled(78), w - scaled(32), scaled(36));
        ctx.Fill();

        // Main content surface.
        ctx.SetSourceRGBA(0.059, 0.082, 0.110, 0.99);
        RoundRectangle(ctx, x + scaled(16), y + scaled(119), w - scaled(32), Math.Max(scaled(8), h - scaled(135)), scaled(6));
        ctx.Fill();

        // Crisp separators distinguish the workspace bands without another texture.
        ctx.LineWidth = scaled(1);
        ctx.SetSourceRGBA(0.55, 0.67, 0.78, 0.12);
        ctx.MoveTo(x + scaled(18), y + scaled(77.5));
        ctx.LineTo(x + w - scaled(18), y + scaled(77.5));
        ctx.Stroke();
        ctx.MoveTo(x + scaled(18), y + scaled(114.5));
        ctx.LineTo(x + w - scaled(18), y + scaled(114.5));
        ctx.Stroke();

        // Thin panel borders.
        ctx.LineWidth = scaled(1);
        ctx.SetSourceRGBA(0.306, 0.384, 0.463, 0.48);
        RoundRectangle(ctx, x + scaled(0.5), y + scaled(0.5), w - scaled(1), h - scaled(1), scaled(8));
        ctx.Stroke();
        ctx.SetSourceRGBA(0.306, 0.384, 0.463, 0.23);
        RoundRectangle(ctx, x + scaled(16.5), y + scaled(119.5), w - scaled(33), Math.Max(scaled(7), h - scaled(136)), scaled(6));
        ctx.Stroke();

        // A short title accent leaves space for the title and close control.
        ctx.SetSourceRGBA(0.49, 0.714, 1.0, section == "anticheat" ? 0.94 : 0.80);
        RoundRectangle(ctx, x + scaled(10), y + scaled(11), scaled(3), scaled(17), scaled(1.5));
        ctx.Fill();
    }

}
