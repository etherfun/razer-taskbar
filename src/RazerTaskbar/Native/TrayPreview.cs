// `razer-taskbar.exe --tray-preview`: standalone visual check for the tray
// icon styles, the same way --hid-probe is a standalone diagnostic. Renders
// both `tray_display_mode` styles at the three DPI-relevant target sizes
// (1x/2x/4x = 16/32/64 px) for the interesting battery states and writes
// upscaled PNG sheets to %APPDATA%\razer-taskbar\tray-preview\, each one on a
// dark and a light strip so antialiasing fringes are visible.
//
// Runs before the single-instance guard (AppHost.Run): it never touches the UI
// or the shell, so it is safe while the resident app is running.

using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Kernel32;

namespace RazerTaskbar.Native;

internal static class TrayPreview
{
    /// <summary>Nearest-neighbour upscale of the rendered icon for inspection;
    /// the icon itself is 16px at 100% DPI, too small to review at 1:1.</summary>
    private const int Upscale = 4;

    private const int CellGap = 6;
    private const int Margin = 8;

    /// <summary>Taskbar-like backgrounds (#202020 dark, #F2F2F2 light); the
    /// AA edge of the digits is the thing to compare between them.</summary>
    private const uint DarkBackground = 0x0020_2020;
    private const uint LightBackground = 0x00F2_F2F2;

    private static readonly (string Label, TrayIconState? State)[] States =
    [
        ("7%", new TrayIconState(7, false, true, false)),
        ("29%", new TrayIconState(29, false, true, false)),
        ("55%", new TrayIconState(55, false, true, false)),
        ("74%", new TrayIconState(74, false, true, false)),
        ("100%", new TrayIconState(100, false, true, false)),
        ("55% charging", new TrayIconState(55, true, true, false)),
        ("no device", null),
        ("55% saver", new TrayIconState(55, false, true, true)),
        ("55% offline", new TrayIconState(55, false, false, false)),
    ];

    public static int Run()
    {
        AttachParentConsole();
        var dir = Path.Combine(AppDataDir(), "razer-taskbar", "tray-preview");
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            $"tray-preview {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"columns: {string.Join(" | ", States.Select(s => s.Label))}",
            "rows: top = dark background (#202020), bottom = light background (#F2F2F2)",
            $"each icon is upscaled {Upscale}x nearest-neighbour for inspection",
            "",
        };
        int failures = 0;
        int sysSize = GetSystemMetricsSafe();
        lines.Add($"SM_CXSMICON at the current DPI: {sysSize}px");
        lines.Add("");
        foreach (var mode in new[] { TrayDisplayMode.Icon, TrayDisplayMode.Number })
        {
            foreach (var colorize in new[] { false, true })
            {
                foreach (var size in new[] { 16, 32, 64 })
                {
                    var path = Path.Combine(
                        dir, $"tray-preview-{mode.ToConfig()}-{(colorize ? "color" : "mono")}-{size}px.png");
                    var sheet = ComposeSheet(mode, size, colorize);
                    if (sheet is null)
                    {
                        lines.Add($"{Path.GetFileName(path)}: FAILED (GDI render returned null)");
                        failures++;
                        continue;
                    }
                    var (width, height, pixels) = sheet.Value;
                    WritePng(path, width, height, pixels);
                    lines.Add($"{Path.GetFileName(path)}  {width}x{height}");
                }
            }
        }
        var manifest = Path.Combine(dir, "tray-preview.txt");
        // The sheets only cover the pixel path; build the real HICONs too
        // (CreateIconIndirect + mask) and destroy them again, so a broken
        // handle path fails the probe instead of surfacing at deploy time.
        int iconChecks = 0;
        int iconOk = 0;
        foreach (var mode in new[] { TrayDisplayMode.Icon, TrayDisplayMode.Number })
        {
            foreach (var colorize in new[] { false, true })
            {
                foreach (var size in new[] { 16, 32, 64 })
                {
                    foreach (var (_, state) in States)
                    {
                        iconChecks++;
                        if (TrayIcon.TryBuildIcon(mode, state, size, colorize, out var icon))
                        {
                            iconOk++;
                            Interop.User32.DestroyIcon(icon);
                        }
                    }
                }
            }
        }
        lines.Add("");
        lines.Add($"HICON build path: {iconOk}/{iconChecks} built + destroyed");
        if (iconOk != iconChecks)
        {
            failures++;
        }
        File.WriteAllLines(manifest, lines);
        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }
        Console.WriteLine($"[written to {dir}]");
        Console.WriteLine(failures == 0
            ? "tray-preview: ok"
            : $"tray-preview: {failures} render(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>One sheet: every state in a column, dark strip above the light
    /// strip. `colorize` is Settings → Colored battery icon (plain-discharge
    /// level palette on/off; state colors always on). Null when the GDI render
    /// failed for any cell.</summary>
    private static (int Width, int Height, uint[] Pixels)? ComposeSheet(
        TrayDisplayMode mode, int size, bool colorize)
    {
        int cell = size * Upscale;
        int columns = States.Length;
        int width = (Margin * 2) + (columns * cell) + ((columns - 1) * CellGap);
        int height = (Margin * 2) + (2 * cell) + CellGap;
        var sheet = new uint[width * height];
        Fill(sheet, width, 0, 0, width, height, 0x0040_4040); // frame
        Fill(sheet, width, Margin, Margin, width - (Margin * 2), cell, DarkBackground);
        Fill(sheet, width, Margin, Margin + cell + CellGap, width - (Margin * 2), cell, LightBackground);

        for (int i = 0; i < columns; i++)
        {
            var pixels = TrayIcon.RenderPixels(mode, States[i].State, size, colorize);
            if (pixels is null)
            {
                return null;
            }
            int x = Margin + (i * (cell + CellGap));
            Blit(sheet, width, pixels, size, x, Margin, DarkBackground);
            Blit(sheet, width, pixels, size, x, Margin + cell + CellGap, LightBackground);
        }
        return (width, height, sheet);
    }

    /// <summary>Draw `src` (straight alpha, size x size) at `Upscale` and
    /// composite it over `background` (the sheet is opaque; 0x00RRGGBB).</summary>
    private static void Blit(uint[] dst, int dstWidth, uint[] src, int srcSize, int x0, int y0, uint background)
    {
        uint backB = background & 0xFF;
        uint backG = (background >> 8) & 0xFF;
        uint backR = (background >> 16) & 0xFF;
        int cell = srcSize * Upscale;
        for (int y = 0; y < cell; y++)
        {
            int sy = y / Upscale;
            for (int x = 0; x < cell; x++)
            {
                uint p = src[(sy * srcSize) + (x / Upscale)];
                uint a = p >> 24;
                if (a == 0)
                {
                    continue;
                }
                uint b = ((p & 0xFF) * a) + (backB * (255 - a));
                uint g = (((p >> 8) & 0xFF) * a) + (backG * (255 - a));
                uint r = (((p >> 16) & 0xFF) * a) + (backR * (255 - a));
                dst[((y0 + y) * dstWidth) + x0 + x] =
                    (((r + 127) / 255) << 16) | (((g + 127) / 255) << 8) | ((b + 127) / 255);
            }
        }
    }

    private static void Fill(uint[] dst, int dstWidth, int x, int y, int w, int h, uint color)
    {
        for (int row = y; row < y + h; row++)
        {
            for (int col = x; col < x + w; col++)
            {
                dst[(row * dstWidth) + col] = color;
            }
        }
    }

    /// <summary>Minimal PNG writer (RGBA8, filter 0, one IDAT) so the sheets
    /// open in any viewer without a System.Drawing/WIC dependency;
    /// <see cref="System.IO.Compression.ZLibStream"/> supplies the zlib wrapper
    /// the format requires.</summary>
    private static void WritePng(string path, int width, int height, uint[] pixels)
    {
        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: truecolour with alpha
        WriteChunk(file, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
                   raw, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (width * 4)];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // filter: none
                for (int x = 0; x < width; x++)
                {
                    uint v = pixels[(y * width) + x];
                    int i = 1 + (x * 4);
                    row[i] = (byte)((v >> 16) & 0xFF);     // R
                    row[i + 1] = (byte)((v >> 8) & 0xFF);  // G
                    row[i + 2] = (byte)(v & 0xFF);         // B
                    row[i + 3] = 0xFF;                     // opaque sheet
                }
                z.Write(row, 0, row.Length);
            }
        }
        WriteChunk(file, "IDAT", raw.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(0xFFFF_FFFFu, typeBytes);
        crc = Crc32(crc, data) ^ 0xFFFF_FFFFu;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static string AppDataDir()
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA"))
            ? "."
            : Environment.GetEnvironmentVariable("APPDATA")!;

    /// <summary>Log the DPI-scaled metric the production icon uses, if the
    /// metric is sane (the probe itself always renders the fixed 16/32/64
    /// matrix).</summary>
    private static int GetSystemMetricsSafe()
    {
        try
        {
            return Interop.User32.GetSystemMetrics(Interop.Win32Consts.SM_CXSMICON);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void AttachParentConsole()
    {
        try
        {
            if (AttachConsole(0xFFFF_FFFFu))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
        catch
        {
            // Windowed process without a parent console: the manifest file
            // still records everything.
        }
    }
}
