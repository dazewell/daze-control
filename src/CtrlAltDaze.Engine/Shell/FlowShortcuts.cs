using System.Runtime.InteropServices;
using CtrlAltDaze.Core;
using CtrlAltDaze.Runtime.Lifecycle;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace CtrlAltDaze.Engine.Shell;

/// <summary>
/// The engine's side of desktop shortcuts (D-049): where they go, what they start, and the Windows parts —
/// <see cref="ShellLinkStore"/> (IShellLinkW) and <see cref="FlowIconRenderer"/> (the flow's glyph on the app tile).
/// The rules are in <see cref="DesktopShortcuts"/>.
/// </summary>
internal static class FlowShortcuts
{
    /// <param name="configArgument">The config this engine was started with; null for the default one.</param>
    public static DesktopShortcuts Create(string? configArgument, string defaultConfig, Action<string>? log = null)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CtrlAltDaze.exe");
        var stub = InstalledStub();
        // .NET reads the Desktop with SHGetKnownFolderPath(FOLDERID_Desktop), so a OneDrive desktop is the one used.
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
        var icons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName, "shortcuts");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var places = new ShortcutPlaces(desktop, icons, stub ?? exe, stub is null ? [exe] : [stub, exe], configArgument, defaultConfig)
        {
            // Velopack's updater rewrites links here too (D-106); a pin is a copy in the taskbar's own folder.
            CopyFolders =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.DoNotVerify),
                Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned"),
            ],
        };
        return new DesktopShortcuts(places, new ShellLinkStore(), new FlowIconRenderer(), log);
    }

    /// <summary>
    /// Velopack's stub (<c>%LOCALAPPDATA%\CtrlAltDazeApp\Ctrl Alt Daze.exe</c>): it outlives updates, unlike the exe in
    /// <c>current</c>, and passes its arguments on. Null outside an install.
    /// </summary>
    internal static string? InstalledStub()
    {
        var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (!string.Equals(current.Name, "current", StringComparison.OrdinalIgnoreCase) || current.Parent is not { } root)
            return null;
        var stub = Path.Combine(root.FullName, AppInfo.ProductName + ".exe");
        return File.Exists(stub) && File.Exists(Path.Combine(root.FullName, "Update.exe")) ? stub : null;
    }
}

/// <summary>.lnk files through IShellLinkW + IPersistFile, each call on a fresh MTA thread (no COM outlives it).</summary>
internal sealed unsafe class ShellLinkStore : IShellLinkStore
{
    private const int MaxText = 32768;

    public IEnumerable<string> Links(string directory, bool recursive = false) =>
        Directory.EnumerateFiles(directory, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true });

    public ShellLinkData? Read(string path) => Mta.Run(() =>
    {
        var link = CreateLink();
        IPersistFile* file = null;
        try
        {
            file = PersistFile(link);
            fixed (char* p = path)
                file->Load(p, STGM.STGM_READ);
            var buffer = new char[MaxText];
            fixed (char* b = buffer)
            {
                // Raw: the path as written, not resolved (a missing target is still ours to list).
                link->GetPath(b, MaxText, null, (uint)SLGP_FLAGS.SLGP_RAWPATH);
                var target = new string(b);
                buffer.AsSpan().Clear();
                link->GetArguments(b, MaxText);
                var arguments = new string(b);
                buffer.AsSpan().Clear();
                int index;
                link->GetIconLocation(b, MaxText, &index);
                var icon = new string(b);
                return new ShellLinkData(target, arguments, icon.Length > 0 ? icon : null);
            }
        }
        catch (COMException)
        {
            return null; // not a link, or one Windows can't read: not ours
        }
        finally
        {
            if (file is not null)
                file->Release();
            link->Release();
        }
    });

    public void Write(string path, ShellLinkData data) => Mta.Run(() =>
    {
        var link = CreateLink();
        IPersistFile* file = null;
        try
        {
            fixed (char* target = data.Target)
                link->SetPath(target);
            fixed (char* arguments = data.Arguments)
                link->SetArguments(arguments);
            if (data.IconPath is { } icon)
            {
                fixed (char* p = icon)
                    link->SetIconLocation(p, 0);
            }
            if (data.Description is { } description)
            {
                fixed (char* p = description)
                    link->SetDescription(p);
            }
            if (data.WorkingDirectory is { } directory)
            {
                fixed (char* p = directory)
                    link->SetWorkingDirectory(p);
            }
            file = PersistFile(link);
            fixed (char* p = path)
                file->Save(p, true);
            // Explorer redraws the item (an Update keeps the path but may change the icon).
            fixed (char* p = path)
                PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_UPDATEITEM, SHCNF_FLAGS.SHCNF_PATHW, p, null);
            return true;
        }
        finally
        {
            if (file is not null)
                file->Release();
            link->Release();
        }
    });

    public void Delete(string path) => File.Delete(path);

    private static IShellLinkW* CreateLink()
    {
        PInvoke.CoCreateInstance(typeof(ShellLink).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IShellLinkW* link).ThrowOnFailure();
        return link;
    }

    private static IPersistFile* PersistFile(IShellLinkW* link)
    {
        var iid = IPersistFile.IID_Guid;
        void* file;
        ((IUnknown*)link)->QueryInterface(&iid, &file).ThrowOnFailure();
        return (IPersistFile*)file;
    }
}

/// <summary>
/// The flow's glyph (Segoe Fluent Icons, white) on the app tile, at the sizes Explorer asks for. DirectWrite through a
/// software Direct2D DC target renders the glyph's coverage; the tile is painted like the tray icon (D-012, D-027).
/// d2d1 and DWrite are loaded for the call and freed after it (idle = zero).
/// </summary>
internal sealed unsafe class FlowIconRenderer : IFlowIconRenderer
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 256];

    /// <summary>Glyph height as a share of the tile, like a Start tile's icon.</summary>
    private const float GlyphShare = 0.56f;

    public void Write(string path, string glyph)
    {
        var images = Render(glyph);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            IconFile.Write(stream, images);
        File.Move(temp, path, overwrite: true);
    }

    internal static List<IconImage> Render(string glyph)
    {
        var d2dModule = NativeLibrary.Load("d2d1.dll");
        var dwriteModule = NativeLibrary.Load("DWrite.dll");
        ID2D1Factory* d2d = null;
        IDWriteFactory* dwrite = null;
        try
        {
            var createD2d = (delegate* unmanaged[Stdcall]<D2D1_FACTORY_TYPE, Guid*, D2D1_FACTORY_OPTIONS*, void**, HRESULT>)
                NativeLibrary.GetExport(d2dModule, "D2D1CreateFactory");
            void* factory;
            var iid = ID2D1Factory.IID_Guid;
            createD2d(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, null, &factory).ThrowOnFailure();
            d2d = (ID2D1Factory*)factory;
            var createDwrite = (delegate* unmanaged[Stdcall]<DWRITE_FACTORY_TYPE, Guid*, void**, HRESULT>)
                NativeLibrary.GetExport(dwriteModule, "DWriteCreateFactory");
            iid = IDWriteFactory.IID_Guid;
            createDwrite(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_ISOLATED, &iid, &factory).ThrowOnFailure();
            dwrite = (IDWriteFactory*)factory;

            var images = new List<IconImage>(Sizes.Length);
            foreach (var size in Sizes)
                images.Add(new IconImage(size, Compose(TrayGlyph.PaintTile(size), Coverage(d2d, dwrite, glyph, size))));
            return images;
        }
        finally
        {
            if (dwrite is not null)
                dwrite->Release();
            if (d2d is not null)
                d2d->Release();
            NativeLibrary.Free(dwriteModule);
            NativeLibrary.Free(d2dModule);
        }
    }

    /// <summary>White glyph coverage over the straight-alpha tile.</summary>
    internal static uint[] Compose(uint[] tile, byte[] coverage)
    {
        var pixels = new uint[tile.Length];
        for (var i = 0; i < tile.Length; i++)
        {
            var a = coverage[i] / 255.0;
            var t = tile[i];
            var ta = (t >> 24) / 255.0 * (1 - a);
            var oa = a + ta;
            if (oa <= 0)
                continue;
            uint Mix(int shift) => (uint)Math.Round((255 * a + ((t >> shift) & 0xFF) * ta) / oa);
            pixels[i] = ((uint)Math.Round(oa * 255) << 24) | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }
        return pixels;
    }

    /// <summary>The glyph's alpha, one byte per pixel, top-down.</summary>
    private static byte[] Coverage(ID2D1Factory* d2d, IDWriteFactory* dwrite, string glyph, int size)
    {
        var props = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96,
            dpiY = 96,
        };
        ID2D1DCRenderTarget* target;
        d2d->CreateDCRenderTarget(&props, &target);
        var dc = PInvoke.CreateCompatibleDC(HDC.Null);
        var bitmap = HBITMAP.Null;
        HGDIOBJ previous = default;
        IDWriteTextFormat* format = null;
        ID2D1SolidColorBrush* brush = null;
        try
        {
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = size,
                    biHeight = -size, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            void* bits;
            bitmap = PInvoke.CreateDIBSection(dc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (bitmap.IsNull)
                throw new IOException("Windows couldn't make a bitmap for the icon.");
            previous = PInvoke.SelectObject(dc, bitmap);
            var rect = new RECT { left = 0, top = 0, right = size, bottom = size };
            target->BindDC(dc, &rect);

            fixed (char* family = "Segoe Fluent Icons")
            fixed (char* locale = "en-us")
                dwrite->CreateTextFormat(family, null, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                    DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    size * GlyphShare, locale, &format);
            format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

            var rt = (ID2D1RenderTarget*)target;
            var white = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
            rt->CreateSolidColorBrush(&white, null, &brush);
            rt->BeginDraw();
            // ClearType needs an opaque background; on a transparent one it's grayscale or nothing.
            rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            var clear = new D2D1_COLOR_F();
            rt->Clear(&clear);
            var layout = new D2D_RECT_F { left = 0, top = 0, right = size, bottom = size };
            fixed (char* text = glyph)
                rt->DrawText(text, (uint)glyph.Length, format, &layout, (ID2D1Brush*)brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            rt->EndDraw(null, null).ThrowOnFailure();

            var coverage = new byte[size * size];
            var source = new ReadOnlySpan<uint>(bits, size * size);
            for (var i = 0; i < coverage.Length; i++)
                coverage[i] = (byte)(source[i] >> 24);
            return coverage;
        }
        finally
        {
            if (brush is not null)
                brush->Release();
            if (format is not null)
                format->Release();
            target->Release();
            if (!previous.IsNull)
                PInvoke.SelectObject(dc, previous);
            if (!bitmap.IsNull)
                PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(dc);
        }
    }
}

/// <summary>COM work on a fresh MTA thread that ends with it: the engine's main thread is STA, pool threads unknown.</summary>
internal static unsafe class Mta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var hr = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                if (hr.Succeeded)
                    PInvoke.CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "CtrlAltDaze shortcuts",
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        return result;
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return true;
    });
}
