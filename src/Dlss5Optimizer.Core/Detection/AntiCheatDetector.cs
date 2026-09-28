using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Detection;

/// <summary>
/// Erkennt Anti-Cheat-Systeme an ihren Dateien und Ordnern. ReShade-/DLL-Injection ist genau das,
/// was diese Systeme melden – im schlimmsten Fall mit Account- oder Hardware-Bann.
/// </summary>
public static class AntiCheatDetector
{
    private static readonly (string Name, string[] Dirs, string[] Files, string[] FilePrefixes)[] Rules =
    [
        ("Easy Anti-Cheat", ["EasyAntiCheat", "EasyAntiCheat_EOS"], ["start_protected_game.exe", "EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_Setup.exe"], []),
        ("BattlEye", ["BattlEye"], [], ["BEService", "BEClient"]),
        ("EA Javelin Anti-Cheat", [], [], ["EAAntiCheat."]),
        ("nProtect GameGuard", ["GameGuard"], ["GameMon.des", "GameMon64.des"], []),
        ("Tencent ACE", ["AntiCheatExpert"], [], []),
        ("XIGNCODE3", ["XIGNCODE"], ["x3.xem"], ["xhunter"]),
        ("mhyprot (HoYoverse)", [], [], ["mhyprot"]),
        ("PunkBuster", [], [], ["pbsvc"]),
        ("Riot Vanguard", [], ["vgk.sys", "vgc.exe"], []),
        ("Denuvo Anti-Cheat", [], [], ["denuvo-anti-cheat"]),
    ];

    // Spiele, deren Anti-Cheat keine eigenen Dateien im Spielordner hat (z. B. VAC).
    private static readonly Dictionary<string, string> KnownExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2.exe"] = "Valve Anti-Cheat (VAC)",
        ["csgo.exe"] = "Valve Anti-Cheat (VAC)",
        ["dota2.exe"] = "Valve Anti-Cheat (VAC)",
        ["VALORANT-Win64-Shipping.exe"] = "Riot Vanguard",
        ["Overwatch.exe"] = "Blizzard Warden",
        ["League of Legends.exe"] = "Riot Vanguard",
        ["r5apex.exe"] = "Easy Anti-Cheat",
        ["FortniteClient-Win64-Shipping.exe"] = "Easy Anti-Cheat / BattlEye",
    };

    public static AntiCheatInfo Detect(FileIndex files, string? mainExe)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, dirs, fileNames, prefixes) in Rules)
        {
            if (dirs.Any(files.HasDirectory)
                || fileNames.Any(files.HasFile)
                || prefixes.Any(p => files.Names.Any(n => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                found.Add(name);
            }
        }
        if (mainExe is not null && KnownExecutables.TryGetValue(Path.GetFileName(mainExe), out var known))
            found.Add(known);
        return new AntiCheatInfo(found.ToList());
    }
}
