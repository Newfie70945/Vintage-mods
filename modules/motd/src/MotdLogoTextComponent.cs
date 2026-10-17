using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace NewfiesCommunityMOTD
{
    /// <summary>
    /// A full-line MOTD logo that participates in GuiElementRichtext layout.
    /// Because it is composed into the rich-text texture, it sits above Home text
    /// and scrolls/clips away exactly like the text beneath it.
    /// </summary>
    internal sealed class MotdLogoTextComponent : RichTextComponentBase
    {
        private readonly string imagePath;
        private readonly int width;
        private readonly int height;
        private readonly int gap;
        private readonly bool preserveAspect;

        public MotdLogoTextComponent(
            ICoreClientAPI api,
            string imagePath,
            int width,
            int height,
            int gap,
            bool preserveAspect) : base(api)
        {
            this.imagePath = imagePath;
            this.width = Math.Max(1, Math.Min(400, width));
            this.height = Math.Max(1, Math.Min(400, height));
            this.gap = Math.Max(0, gap);
            this.preserveAspect = preserveAspect;
            Float = EnumFloat.None;
            BoundsPerLine = Array.Empty<LineRectangled>();
        }

        public override EnumCalcBoundsResult CalcBounds(
            TextFlowPath[] flowPath,
            double currentLineHeight,
            double offsetX,
            double lineY,
            out double nextOffsetX)
        {
            TextFlowPath fp = GetCurrentFlowPathSection(flowPath, lineY)
                ?? (flowPath.Length > 0 ? flowPath[0] : new TextFlowPath(GuiElement.scaled(width)));

            double lineWidth = Math.Max(GuiElement.scaled(width), fp.X2 - fp.X1);
            double lineHeight = GuiElement.scaled(height + gap);

            BoundsPerLine = new[]
            {
                new LineRectangled(fp.X1, lineY, lineWidth, lineHeight)
            };

            nextOffsetX = 0;
            return EnumCalcBoundsResult.Nextline;
        }

        public override void ComposeElements(Context ctx, ImageSurface surface)
        {
            if (BoundsPerLine == null || BoundsPerLine.Length == 0) return;

            try
            {
                using var img = new ImageSurface(imagePath);
                var line = BoundsPerLine[0];

                double boxW = Math.Min(line.Width, GuiElement.scaled(width));
                double boxH = GuiElement.scaled(height);

                double sx = boxW / Math.Max(1, img.Width);
                double sy = boxH / Math.Max(1, img.Height);
                double scaleX = sx;
                double scaleY = sy;

                if (preserveAspect)
                {
                    double scale = Math.Min(sx, sy);
                    scaleX = scaleY = scale;
                }

                double drawW = img.Width * scaleX;
                double drawH = img.Height * scaleY;
                double x = line.X + (line.Width - drawW) / 2.0;
                double y = line.Y + (boxH - drawH) / 2.0;

                ctx.Save();
                ctx.Translate(x, y);
                ctx.Scale(scaleX, scaleY);
                ctx.SetSourceSurface(img, 0, 0);
                ctx.Paint();
                ctx.Restore();
            }
            catch
            {
                // A missing/bad logo must never prevent the MOTD text from rendering.
            }
        }
    }
}
