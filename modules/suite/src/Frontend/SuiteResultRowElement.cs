using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

/// <summary>
/// Subtle alternating result surfaces drawn at their current bounds. The texture
/// is independent of the composer background, so scrolling never leaves a baked
/// stripe behind. Add inside the same clip as the text, then move both row bounds.
/// All rows share two solid one-pixel textures; a long result list does not allocate
/// a full-width image for every record. The last row to dispose releases the pair.
/// </summary>
internal sealed class SuiteResultRowElement : GuiElement
{
    private sealed class SharedSurfaces
    {
        public LoadedTexture Normal;
        public LoadedTexture Alternate;
        public int References;

        public SharedSurfaces(ICoreClientAPI capi)
        {
            Normal = new LoadedTexture(capi);
            Alternate = new LoadedTexture(capi);
        }
    }

    private static readonly Dictionary<ICoreClientAPI, SharedSurfaces> SharedByClient =
        new(ReferenceEqualityComparer.Instance);

    private readonly bool alternate;
    private readonly SharedSurfaces shared;
    private bool disposed;

    private LoadedTexture Texture => alternate ? shared.Alternate : shared.Normal;

    // Native clip start/end markers use DrawOrder=0. Keep their insertion order:
    // each background must be added immediately before its own rich text row.
    // A negative draw order would render all stripes before the scissor begins.
    public override double DrawOrder => 0;

    public SuiteResultRowElement(ICoreClientAPI capi, ElementBounds bounds, bool alternate)
        : base(capi, bounds)
    {
        this.alternate = alternate;
        lock (SharedByClient)
        {
            if (!SharedByClient.TryGetValue(capi, out var existing))
            {
                existing = new SharedSurfaces(capi);
                SharedByClient.Add(capi, existing);
            }
            shared = existing;
            shared.References++;
        }
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        if (Texture.TextureId != 0) return;

        using var rowSurface = new ImageSurface(Format.Argb32, 1, 1);
        using var rowContext = new Context(rowSurface);
        if (alternate)
            rowContext.SetSourceRGBA(0.118, 0.157, 0.196, 0.76);
        else
            rowContext.SetSourceRGBA(0.094, 0.125, 0.157, 0.65);
        rowContext.Paint();
        if (alternate)
            generateTexture(rowSurface, ref shared.Alternate);
        else
            generateTexture(rowSurface, ref shared.Normal);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (disposed || Texture.TextureId == 0) return;
        api.Render.Render2DTexturePremultipliedAlpha(
            Texture.TextureId,
            Bounds.renderX,
            Bounds.renderY,
            Bounds.InnerWidth,
            Bounds.InnerHeight);
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (SharedByClient)
        {
            if (--shared.References == 0)
            {
                SharedByClient.Remove(api);
                shared.Normal.Dispose();
                shared.Alternate.Dispose();
            }
        }
        base.Dispose();
    }
}
