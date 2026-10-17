using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

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

	private static readonly Dictionary<ICoreClientAPI, SharedSurfaces> SharedByClient = new Dictionary<ICoreClientAPI, SharedSurfaces>(ReferenceEqualityComparer.Instance);

	private readonly bool alternate;

	private readonly SharedSurfaces shared;

	private bool disposed;

	private LoadedTexture Texture => alternate ? shared.Alternate : shared.Normal;

	public override double DrawOrder => 0.0;

	public SuiteResultRowElement(ICoreClientAPI capi, ElementBounds bounds, bool alternate)
		: base(capi, bounds)
	{
		this.alternate = alternate;
		lock (SharedByClient)
		{
			if (!SharedByClient.TryGetValue(capi, out SharedSurfaces value))
			{
				value = new SharedSurfaces(capi);
				SharedByClient.Add(capi, value);
			}
			shared = value;
			shared.References++;
		}
	}

	public override void ComposeElements(Context ctx, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();
		if (Texture.TextureId != 0)
		{
			return;
		}
		using ImageSurface surface2 = new ImageSurface(Format.Argb32, 1, 1);
		using Context context = new Context(surface2);
		if (alternate)
		{
			context.SetSourceRGBA(0.118, 0.157, 0.196, 0.76);
		}
		else
		{
			context.SetSourceRGBA(0.094, 0.125, 0.157, 0.65);
		}
		context.Paint();
		if (alternate)
		{
			generateTexture(surface2, ref shared.Alternate);
		}
		else
		{
			generateTexture(surface2, ref shared.Normal);
		}
	}

	public override void RenderInteractiveElements(float deltaTime)
	{
		if (!disposed && Texture.TextureId != 0)
		{
			api.Render.Render2DTexturePremultipliedAlpha(Texture.TextureId, Bounds.renderX, Bounds.renderY, Bounds.InnerWidth, Bounds.InnerHeight);
		}
	}

	public override void Dispose()
	{
		if (disposed)
		{
			return;
		}
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
