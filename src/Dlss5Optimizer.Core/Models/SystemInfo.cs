using System.Globalization;
using System.Text.RegularExpressions;

namespace Dlss5Optimizer.Core.Models;

public enum GpuFamily
{
    Unknown,
    NonNvidia,
    NvidiaNoTensor,
    Rtx20,
    Rtx30,
    Rtx40,
    Rtx50,
}

/// <summary>Grafikkarte, wie sie Windows meldet, plus abgeleitete Eigenschaften.</summary>
public sealed partial record GpuInfo(string Name, Version? DriverVersion, long VramBytes)
{
    // KI-Leistung (AI TOPS laut NVIDIA-Datenblatt) als Maß für die Tensor-Leistung. Daraus wird
    // der Startwert für die DLSS-5-Kosten geschätzt, bis ein Benchmark echte Werte liefert.
    private static readonly (string Model, double Tops)[] TopsTable =
    [
        ("5090 Laptop", 1824), ("5080 Laptop", 1334), ("5070 Ti Laptop", 992), ("5070 Laptop", 798),
        ("5060 Laptop", 572), ("5050 Laptop", 440),
        ("5090", 3352), ("5080", 1801), ("5070 Ti", 1406), ("5070", 988), ("5060 Ti", 759), ("5060", 614), ("5050", 421),
    ];

    [GeneratedRegex(@"RTX\s*(PRO\s*)?(\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtxModel();

    [GeneratedRegex(@"RTX\s*PRO\s*\d{4}\s*Blackwell|RTX\s*PRO\s*\d{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtxProBlackwell();

    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    public bool IsLaptop => Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || Name.Contains("Mobile", StringComparison.OrdinalIgnoreCase);

    public GpuFamily Family
    {
        get
        {
            if (!IsNvidia)
                return string.IsNullOrWhiteSpace(Name) ? GpuFamily.Unknown : GpuFamily.NonNvidia;
            if (RtxProBlackwell().IsMatch(Name))
                return GpuFamily.Rtx50;
            var m = RtxModel().Match(Name);
            if (!m.Success)
                return GpuFamily.NvidiaNoTensor;
            int series = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 1000;
            return series switch
            {
                2 => GpuFamily.Rtx20,
                3 => GpuFamily.Rtx30,
                4 => GpuFamily.Rtx40,
                >= 5 => GpuFamily.Rtx50,
                _ => GpuFamily.NvidiaNoTensor,
            };
        }
    }

    /// <summary>AI TOPS der Karte; unbekannte RTX-50-Modelle bekommen den Wert der RTX 5070.</summary>
    public double TensorTops
    {
        get
        {
            foreach (var (model, tops) in TopsTable)
            {
                bool wantLaptop = model.EndsWith("Laptop", StringComparison.Ordinal);
                var core = wantLaptop ? model[..^" Laptop".Length] : model;
                if (wantLaptop != IsLaptop)
                    continue;
                // "5070" darf nicht auf "5070 Ti" passen.
                var pattern = $@"\b{Regex.Escape(core)}\b(?!\s*Ti)";
                if (core.EndsWith("Ti", StringComparison.Ordinal))
                    pattern = $@"\b{Regex.Escape(core)}\b";
                if (Regex.IsMatch(Name, pattern, RegexOptions.IgnoreCase))
                    return tops;
            }
            return 988;
        }
    }
}

public sealed record DisplayInfo(int Width, int Height, int RefreshHz)
{
    public static DisplayInfo Default { get; } = new(2560, 1440, 144);
    public double Megapixels => Width * (double)Height / 1_000_000.0;
}

public sealed record SystemInfo(GpuInfo Gpu, DisplayInfo Display, bool? HardwareSchedulingEnabled = null)
{
    /// <summary>Ältester Treiber, mit dem das DLSS-5-Modell (nvngx_dlssnr.dll 310.8.0) in den Referenz-Setups läuft.</summary>
    public static readonly Version MinDlss5Driver = new(616, 56);
}

public static class NvidiaDriverVersion
{
    /// <summary>
    /// Wandelt die Windows-Treiberversion (z. B. "32.0.16.1664") in die NVIDIA-Schreibweise (616.64) um:
    /// die letzten fünf Ziffern der beiden letzten Blöcke.
    /// </summary>
    public static Version? FromWindowsVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion))
            return null;
        // Windows-Treiberversionen haben immer vier Blöcke (z. B. 32.0.16.1664).
        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4)
            return null;
        var digits = parts[^2] + parts[^1].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsDigit))
            return null;
        var last5 = digits[^5..];
        return new Version(int.Parse(last5[..3], CultureInfo.InvariantCulture), int.Parse(last5[3..], CultureInfo.InvariantCulture));
    }
}
