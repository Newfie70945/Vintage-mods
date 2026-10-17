using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY"));
string modPath = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { Path.GetDirectoryName(modPath)!, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string file = Path.Combine(root, name.Name + ".dll"); if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
// A real client installs native GUI resolvers. This headless fixture uses system Cairo.
var cairo = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Lib", "cairo-sharp.dll"));
if (OperatingSystem.IsLinux()) NativeLibrary.SetDllImportResolver(cairo,
    (name, assembly, search) => name == "libcairo-2" ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero);
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
RenderingChecks.Run(mod);

static class RenderingChecks
{
    // Keep game/Cairo types in a separate method so assembly resolution is installed before JIT.
    public static void Run(Assembly mod)
    {
        var cellType = mod.GetType("BlockLogger.Frontend.SuiteProfilerTextElement", true)!;
        int legacyLeaks = 0, cases = 0;
        foreach (float scale in new[] { 0.75f, 1f, 1.5f, 2f })
        {
            Vintagestory.API.Config.RuntimeEnv.GUIScale = scale;
            foreach (string text in new[] {
                "Very long mod name with attribution details and several words repeated repeated repeated repeated",
                "server/main/tick/Worldgen/WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW",
                "1234567890123456789012345678901234567890.999ms",
                "Задержка сервера и очень длинный диагностический маркер с дополнительными сведениями",
                "Combining e\u0301 and emoji 👨‍👩‍👧‍👦 repeated repeated repeated repeated",
                "Evidence\nsecond line\nthird line\nfourth line",
                "Short value"
            }) foreach (int height in new[] { 24, 48 })
            {
                using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, 640, 480);
                using var ctx = new Cairo.Context(surface);
                var rootBounds = Vintagestory.API.Client.ElementBounds.Fixed(0, 0, 640, 480);
                rootBounds.IsDrawingSurface = true; rootBounds.CalcWorldBounds();
                var bounds = Vintagestory.API.Client.ElementBounds.Fixed(40, 30, 140, height);
                bounds.ParentBounds = rootBounds;
                var font = Vintagestory.API.Client.CairoFont.WhiteSmallText();
                var cell = (Vintagestory.API.Client.GuiElementStaticText)Activator.CreateInstance(cellType, new object?[] { null, text, font, bounds })!;
                bounds.CalcWorldBounds(); double originalHeight = bounds.absInnerHeight;
                cell.ComposeElements(ctx, surface); surface.Flush();
                try { CheckPixels(surface, bounds, true); } catch (Exception ex) { throw new Exception($"scale={scale}, height={height}, text={text}: {ex.Message}", ex); }
                if (bounds.absInnerHeight != originalHeight) throw new Exception("Native wrapping changed fixed row height");
                if (text == "Short value" && cell.Text != text) throw new Exception("Short values were changed");
                if (height == 24 && cell.Text.Contains('\n')) throw new Exception("A table cell retained line breaks");
                using var oldSurface = new Cairo.ImageSurface(Cairo.Format.Argb32, 640, 480);
                using var oldCtx = new Cairo.Context(oldSurface);
                var oldBounds = Vintagestory.API.Client.ElementBounds.Fixed(40, 30, 140, height);
                oldBounds.ParentBounds = rootBounds; oldBounds.CalcWorldBounds();
                var oldCell = new Vintagestory.API.Client.GuiElementStaticText(null!, text, Vintagestory.API.Client.EnumTextOrientation.Left, oldBounds, font);
                oldCell.ComposeElements(oldCtx, oldSurface); oldSurface.Flush();
                // Native text changes its bounds; compare against the declared new cell rectangle.
                legacyLeaks += CheckPixels(oldSurface, bounds, false) > 0 ? 1 : 0;
                cases++;
            }
        }
        foreach (float scale in new[] { 0.75f, 1f, 1.5f, 2f })
        {
            Vintagestory.API.Config.RuntimeEnv.GUIScale=scale;
            using var surface=new Cairo.ImageSurface(Cairo.Format.Argb32,1800,1600);
            using var ctx=new Cairo.Context(surface);
            var root=Vintagestory.API.Client.ElementBounds.Fixed(0,0,1800,1600);root.IsDrawingSurface=true;root.CalcWorldBounds();
            var bounds=Vintagestory.API.Client.ElementBounds.Fixed(10,10,760,620);bounds.ParentBounds=root;
            using var shell=(Vintagestory.API.Client.GuiElement)Activator.CreateInstance(mod.GetType("BlockLogger.Frontend.SuitePanelElement",true)!,new object?[]{null,bounds,"anticheat"})!;
            shell.ComposeElements(ctx,surface);
            // Composition must restore its clip so following GUI elements remain visible.
            ctx.SetSourceRGBA(1,0,0,1);ctx.Rectangle(0,0,2,2);ctx.Fill();surface.Flush();
            if(surface.Data[3]!=255)throw new Exception("Header grain leaked the clip into subsequent elements");
        }
        Console.WriteLine("PASS UI2: real Shift+L shell composes at four GUI scales and restores the Cairo clip for subsequent controls.");
        if (legacyLeaks == 0) throw new Exception("Fixtures failed to reproduce vanilla text overflow");
        Console.WriteLine($"PASS: {cases} real Cairo cell renders at 0.75/1/1.5/2 GUI scales stay within fixed columns/rows; vanilla overflow reproduced in {legacyLeaks} fixtures. No client/GL context created.");
    }

    static int CheckPixels(Cairo.ImageSurface surface, Vintagestory.API.Client.ElementBounds bounds, bool assertBounded)
    {
        byte[] pixels = surface.Data; int outside = 0, ink = 0;
        for (int y = 0; y < surface.Height; y++) for (int x = 0; x < surface.Width; x++)
        {
            if (pixels[y * surface.Stride + x * 4 + 3] == 0) continue;
            ink++;
            if (x + 1 <= bounds.drawX || x >= bounds.drawX + bounds.InnerWidth || y + 1 <= bounds.drawY || y >= bounds.drawY + bounds.InnerHeight) { outside++; if (assertBounded) Console.WriteLine($"outside pixel {x},{y} alpha={pixels[y*surface.Stride+x*4+3]} bounds={bounds.drawX},{bounds.drawY},{bounds.InnerWidth},{bounds.InnerHeight}"); }
        }
        if (ink == 0) throw new Exception("Fixture drew no text");
        if (assertBounded && outside != 0) throw new Exception($"Profiler text escaped its cell: {outside} pixels");
        return outside;
    }
}
