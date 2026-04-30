using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Capture;

/// <summary>
/// Live Notetaker — periodic screen captures stored as a timeline. Windows ships PrintWindow-based
/// capture; mobile and other desktops record a timeline of text notes only. Captures are written to
/// {data_dir}/notes/{session-id}/ with a session.json manifest.
/// </summary>
public sealed class LiveNotetaker : ILiveNotetaker, IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<LiveNotetaker> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _sessionDir;
    private string? _sessionId;
    private DateTime _sessionStart;
    private int _frameCount;

    public LiveNotetaker(ISettingsService settings, ILogger<LiveNotetaker> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsRecording => _loop is { IsCompleted: false };
    public string? CurrentSessionDirectory => _sessionDir;

    private string RootDir
    {
        get
        {
            var baseDir = string.IsNullOrWhiteSpace(_settings.DataDirectory) ? Microsoft.Maui.Storage.FileSystem.AppDataDirectory : _settings.DataDirectory;
            return Path.Combine(baseDir, "notes");
        }
    }

    public Task<string> StartAsync(string? label, TimeSpan interval, CancellationToken ct = default)
    {
        if (IsRecording) throw new InvalidOperationException("Already recording");
        _sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        _sessionDir = Path.Combine(RootDir, _sessionId);
        Directory.CreateDirectory(_sessionDir);
        _sessionStart = DateTime.UtcNow;
        _frameCount = 0;

        File.WriteAllText(Path.Combine(_sessionDir, "session.json"),
            $"{{\"id\":\"{_sessionId}\",\"label\":{JsonSerializer.Serialize(label)},\"started\":\"{_sessionStart:O}\"}}");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        var step = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;

        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CaptureFrame();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Notetaker capture frame failed");
                }
                try { await Task.Delay(step, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);

        return Task.FromResult(_sessionDir);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        if (_sessionDir is not null)
        {
            var manifest = Path.Combine(_sessionDir, "session.json");
            try
            {
                File.AppendAllText(manifest, Environment.NewLine + $"{{\"ended\":\"{DateTime.UtcNow:O}\",\"frames\":{_frameCount}}}");
            }
            catch { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public Task<string> CaptureOnceAsync(string? note, CancellationToken ct = default)
    {
        var dir = _sessionDir ?? Path.Combine(RootDir, "adhoc");
        Directory.CreateDirectory(dir);
        var path = CaptureFrameTo(dir, note);
        return Task.FromResult(path);
    }

    public IReadOnlyList<NoteSession> ListSessions()
    {
        var root = RootDir;
        if (!Directory.Exists(root)) return Array.Empty<NoteSession>();
        var list = new List<NoteSession>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            DateTime started = Directory.GetCreationTimeUtc(dir);
            int frames = Directory.EnumerateFiles(dir, "*.png").Count()
                       + Directory.EnumerateFiles(dir, "*.txt").Count();
            list.Add(new NoteSession(id, dir, started, null, frames, null));
        }
        return list;
    }

    private void CaptureFrame()
    {
        if (_sessionDir is null) return;
        CaptureFrameTo(_sessionDir, null);
    }

    private string CaptureFrameTo(string dir, string? note)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var basePath = Path.Combine(dir, stamp);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var bmp = basePath + ".bmp";
                WindowsScreenCapture.CaptureTo(bmp);
                Interlocked.Increment(ref _frameCount);
                if (!string.IsNullOrEmpty(note))
                    File.WriteAllText(basePath + ".txt", note);
                return bmp;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows capture failed; falling back to text note");
            }
        }

        var txt = basePath + ".txt";
        File.WriteAllText(txt, note ?? $"[no-capture available on this platform] frame {_frameCount + 1}");
        Interlocked.Increment(ref _frameCount);
        return txt;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording) await StopAsync().ConfigureAwait(false);
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPFILEHEADER
    {
        public ushort bfType;
        public uint bfSize;
        public ushort bfReserved1;
        public ushort bfReserved2;
        public uint bfOffBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    public static void CaptureTo(string bmpPath)
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No display area detected");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = SelectObject(memDc, bmp);
        try
        {
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY))
                throw new InvalidOperationException("BitBlt failed");

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(w * h * 4)
            };
            var pixels = new byte[w * h * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                GetDIBits(memDc, bmp, 0, (uint)h, handle.AddrOfPinnedObject(), ref header, 0);
            }
            finally { handle.Free(); }

            WriteBmp(bmpPath, pixels, w, h);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void WriteBmp(string path, byte[] pixels, int w, int h)
    {
        var header = new BITMAPFILEHEADER
        {
            bfType = 0x4D42,
            bfOffBits = (uint)(Marshal.SizeOf<BITMAPFILEHEADER>() + Marshal.SizeOf<BITMAPINFOHEADER>()),
            bfSize = (uint)(Marshal.SizeOf<BITMAPFILEHEADER>() + Marshal.SizeOf<BITMAPINFOHEADER>() + pixels.Length)
        };
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = (uint)pixels.Length
        };
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        WriteStruct(bw, header);
        WriteStruct(bw, info);
        bw.Write(pixels);
    }

    private static void WriteStruct<T>(BinaryWriter bw, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buf = new byte[size];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(value!, h.AddrOfPinnedObject(), false); bw.Write(buf); }
        finally { h.Free(); }
    }
}
