using System.Runtime.InteropServices;
using Dlss5Optimizer.Core.Library;
using Dlss5Optimizer.Core.Models;
using Microsoft.Win32;

namespace Dlss5Optimizer.Platform;

/// <summary>Liest GPU, Treiber, Monitor und relevante Windows-Einstellungen.</summary>
public static class WindowsSystem
{
    // Geräteklasse "Display adapters".
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static SystemInfo Read() => new(ReadGpu(), ReadDisplay(), ReadHags());

    public static GpuInfo ReadGpu()
    {
        GpuInfo? best = null;
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (cls is null)
            return new GpuInfo("", null, 0);

        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            if (k?.GetValue("DriverDesc") is not string name)
                continue;
            var driver = NvidiaDriverVersion.FromWindowsVersion(k.GetValue("DriverVersion") as string);
            long vram = k.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long l => l,
                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                int i => (uint)i,
                _ => 0,
            };
            var gpu = new GpuInfo(name, name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? driver : null, vram);
            // Die NVIDIA-Karte mit dem meisten Speicher gewinnt (Laptops: dGPU statt iGPU).
            if (best is null || gpu.IsNvidia && !best.IsNvidia || gpu.IsNvidia == best.IsNvidia && gpu.VramBytes > best.VramBytes)
                best = gpu;
        }
        return best ?? new GpuInfo("", null, 0);
    }

    public static DisplayInfo ReadDisplay()
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmPelsWidth > 0)
            return new DisplayInfo(mode.dmPelsWidth, mode.dmPelsHeight, Math.Max(60, mode.dmDisplayFrequency));
        return DisplayInfo.Default;
    }

    /// <summary>Hardwarebeschleunigte GPU-Planung: HwSchMode = 2 bedeutet an.</summary>
    public static bool? ReadHags()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        return k?.GetValue("HwSchMode") is int v ? v == 2 : null;
    }

    /// <summary>Windows 11: "Optimierungen für Spiele im Fenstermodus" (Flip-Model für DX10/11).</summary>
    public static bool? ReadWindowedOptimizations()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        if (k?.GetValue("DirectXUserGlobalSettings") is not string s)
            return null;
        return s.Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnableWindowedOptimizations()
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        var current = k.GetValue("DirectXUserGlobalSettings") as string ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("SwapEffectUpgradeEnable=", StringComparison.OrdinalIgnoreCase))
            .Append("SwapEffectUpgradeEnable=1");
        k.SetValue("DirectXUserGlobalSettings", string.Join(";", parts) + ";");
    }

    public static bool? ReadGameMode()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        return k?.GetValue("AutoGameModeEnabled") is int v ? v == 1 : null;
    }

    public static string? SteamRoot()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var path = k?.GetValue("SteamPath") as string;
        if (path is null)
        {
            using var lm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            path = lm?.GetValue("InstallPath") as string;
        }
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    public static string EpicManifests() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    /// <summary>Xbox-App-Spiele liegen standardmäßig in &lt;Laufwerk&gt;:\XboxGames.</summary>
    public static IEnumerable<string> XboxFolders() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames"))
            .Where(Directory.Exists);

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}

/// <summary>GOG Galaxy trägt installierte Spiele in die Registry ein.</summary>
public sealed class GogLibraryScanner : ILibraryScanner
{
    public string Name => "GOG";

    public IEnumerable<GameInfo> Scan()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root is null)
            yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            var name = k?.GetValue("gameName") as string;
            var path = k?.GetValue("path") as string;
            var exe = k?.GetValue("exe") as string;
            if (name is not null && path is not null && Directory.Exists(path))
                yield return new GameInfo(name, path, GameSource.Gog, id, exe);
        }
    }
}
