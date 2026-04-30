using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Desktop Control — virtual mouse/keyboard via platform-native input injection. Phase 18 ships the
/// Windows path (SendInput); other platforms return a supported-platform error. User physical input
/// always has priority — the runtime must never race a human operator.
/// </summary>
public sealed class DesktopControlPlugin : IBuiltInPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<DesktopControlPlugin> _logger;

    public DesktopControlPlugin(ILogger<DesktopControlPlugin> logger)
    {
        _logger = logger;
    }

    public string Name => "desktop-control";
    public string Description => "Synthetic mouse/keyboard input + screen capture (Windows)";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        DesktopControlCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<DesktopControlCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        if (!OperatingSystem.IsWindows())
            return new PluginInvocationResult(false, string.Empty, "desktop-control is only supported on Windows in this build");

        try
        {
            // Accept several common synonyms so the AI doesn't have to guess the exact verb.
            var action = (cmd.Action ?? string.Empty).ToLowerInvariant();
            return action switch
            {
                "mouse-move" or "move" or "cursor-move"        => WindowsMouseMove(cmd),
                "mouse-click" or "click"                       => WindowsMouseClick(cmd),
                "type" or "type-text" or "input" or "send-keys" => await WindowsTypeTextAsync(cmd, ct).ConfigureAwait(false),
                "key-press" or "press" or "key" or "shortcut"  => WindowsKeyPress(cmd),
                "cursor-pos" or "get-cursor"                   => WindowsCursorPos(),
                "screenshot" or "capture-screen" or "screen"   => WindowsScreenshot(cmd),
                _ => new PluginInvocationResult(false, string.Empty, "unknown action: " + cmd.Action)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "desktop-control action failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static PluginInvocationResult WindowsMouseMove(DesktopControlCommand cmd)
    {
        if (cmd.X is null || cmd.Y is null)
            return new PluginInvocationResult(false, string.Empty, "mouse-move requires x and y");
        SetCursorPos(cmd.X.Value, cmd.Y.Value);
        return new PluginInvocationResult(true, $"cursor moved to {cmd.X},{cmd.Y}");
    }

    [SupportedOSPlatform("windows")]
    private static PluginInvocationResult WindowsMouseClick(DesktopControlCommand cmd)
    {
        var button = (cmd.Button ?? "left").ToLowerInvariant();
        var (down, up) = button switch
        {
            "right" => ((uint)MOUSEEVENTF_RIGHTDOWN, (uint)MOUSEEVENTF_RIGHTUP),
            "middle" => ((uint)MOUSEEVENTF_MIDDLEDOWN, (uint)MOUSEEVENTF_MIDDLEUP),
            _ => ((uint)MOUSEEVENTF_LEFTDOWN, (uint)MOUSEEVENTF_LEFTUP)
        };
        if (cmd.X is not null && cmd.Y is not null) SetCursorPos(cmd.X.Value, cmd.Y.Value);
        SendMouseInput(down);
        SendMouseInput(up);
        return new PluginInvocationResult(true, $"{button} click at cursor");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<PluginInvocationResult> WindowsTypeTextAsync(DesktopControlCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cmd.Text))
            return new PluginInvocationResult(false, string.Empty, "type-text requires 'text'");
        var perCharDelay = Math.Clamp(cmd.Delay.GetValueOrDefault(0), 0, 5000);
        foreach (var ch in cmd.Text)
        {
            ct.ThrowIfCancellationRequested();
            SendUnicode(ch);
            if (perCharDelay > 0) await Task.Delay(perCharDelay, ct).ConfigureAwait(false);
        }
        return new PluginInvocationResult(true, $"typed {cmd.Text.Length} character(s)");
    }

    [SupportedOSPlatform("windows")]
    private static PluginInvocationResult WindowsScreenshot(DesktopControlCommand cmd)
    {
#if WINDOWS
        // Capture the primary screen (or all virtual screens if `cmd.Region == "all"`) and return
        // a markdown image with a base64 data URL. The chat will render it inline.
        try
        {
            int x, y, width, height;
            if (string.Equals(cmd.Region, "all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cmd.Region, "virtual", StringComparison.OrdinalIgnoreCase))
            {
                x = GetSystemMetrics(SM_XVIRTUALSCREEN);
                y = GetSystemMetrics(SM_YVIRTUALSCREEN);
                width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            }
            else
            {
                x = 0; y = 0;
                width = GetSystemMetrics(SM_CXSCREEN);
                height = GetSystemMetrics(SM_CYSCREEN);
            }
            if (width <= 0 || height <= 0)
                return new PluginInvocationResult(false, string.Empty, "screen size unavailable");

            using var bmp = new global::System.Drawing.Bitmap(width, height);
            using (var g = global::System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new global::System.Drawing.Size(width, height));
            using var ms = new MemoryStream();
            bmp.Save(ms, global::System.Drawing.Imaging.ImageFormat.Png);
            var b64 = Convert.ToBase64String(ms.ToArray());
            var output = $"![screenshot {width}x{height}](data:image/png;base64,{b64})";
            return new PluginInvocationResult(true, output);
        }
        catch (Exception ex)
        {
            return new PluginInvocationResult(false, string.Empty, "screenshot failed: " + ex.Message);
        }
#else
        return new PluginInvocationResult(false, string.Empty, "screenshot is only supported on Windows");
#endif
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern int GetSystemMetrics(int nIndex);

    [SupportedOSPlatform("windows")]
    private static PluginInvocationResult WindowsKeyPress(DesktopControlCommand cmd)
    {
        if (string.IsNullOrEmpty(cmd.Key))
            return new PluginInvocationResult(false, string.Empty, "key-press requires 'key'");
        if (!TryMapKey(cmd.Key, out var vk))
            return new PluginInvocationResult(false, string.Empty, "unknown key: " + cmd.Key);
        SendVirtualKey(vk);
        return new PluginInvocationResult(true, $"pressed {cmd.Key}");
    }

    [SupportedOSPlatform("windows")]
    private static PluginInvocationResult WindowsCursorPos()
    {
        GetCursorPos(out var pt);
        return new PluginInvocationResult(true, JsonSerializer.Serialize(new { x = pt.X, y = pt.Y }));
    }

    private static bool TryMapKey(string key, out ushort vk)
    {
        vk = key.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "home" => 0x24,
            "end" => 0x23,
            _ when key.Length == 1 => (ushort)char.ToUpperInvariant(key[0]),
            _ => 0
        };
        return vk != 0;
    }

    #region Win32 P/Invoke

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg; public ushort wParamL; public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [SupportedOSPlatform("windows")]
    private static void SendMouseInput(uint flags)
    {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi.dwFlags = flags;
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    [SupportedOSPlatform("windows")]
    private static void SendVirtualKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [SupportedOSPlatform("windows")]
    private static void SendUnicode(char ch)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wScan = ch;
        inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wScan = ch;
        inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    #endregion

    private sealed class DesktopControlCommand
    {
        public string? Action { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public string? Button { get; set; }
        public string? Text { get; set; }
        public string? Key { get; set; }
        /// <summary>Per-character delay in ms for type/type-text. Clamped to [0, 5000].</summary>
        public int? Delay { get; set; }
        /// <summary>Screenshot region: "primary" (default) or "all"/"virtual" for the full virtual screen.</summary>
        public string? Region { get; set; }
    }
}
