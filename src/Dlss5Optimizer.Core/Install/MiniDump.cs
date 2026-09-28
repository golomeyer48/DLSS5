namespace Dlss5Optimizer.Core.Install;

/// <summary>Das Wichtigste aus einem Absturzabbild: Ausnahme, Ort und die Module auf dem Stack.</summary>
/// <param name="FaultModule">Modul, in dem die Ausnahme passierte, oder null (Adresse außerhalb jedes Moduls).</param>
/// <param name="StackModules">Module der Rücksprungadressen auf dem Stack, oben zuerst, ohne direkte Wiederholungen.</param>
public sealed record CrashDumpInfo(uint Code, ulong Address, string? FaultModule, IReadOnlyList<string> StackModules)
{
    public string CodeHex => $"0x{Code:X8}";
}

/// <summary>
/// Liest Windows-Minidumps (MINIDUMP_*-Strukturen) ohne Debugger – genug, um zu sehen, wer den Absturz
/// ausgelöst hat. Der Stack wird nicht abgewickelt, sondern nach Rücksprungadressen in Modulen durchsucht;
/// das ist ungenau, zeigt den Aufrufer ganz oben aber verlässlich (so liest es auch der Feeder-Autor).
/// </summary>
public static class MiniDump
{
    private const uint Signature = 0x504D444D; // "MDMP"
    private const uint ModuleList = 4, MemoryList = 5, Exception = 6, Memory64List = 9, SystemInfo = 7;
    private const int StackScanBytes = 0x2000;

    public static CrashDumpInfo? TryRead(string path, int maxStackModules = 12)
    {
        try
        {
            return Read(File.ReadAllBytes(path), maxStackModules);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static CrashDumpInfo? Read(byte[] b, int maxStackModules = 12)
    {
        if (b.Length < 32 || U32(b, 0) != Signature)
            return null;
        var streams = new Dictionary<uint, (uint Size, uint Rva)>();
        uint count = U32(b, 8), dir = U32(b, 12);
        for (uint i = 0; i < count; i++)
        {
            long e = dir + i * 12;
            streams.TryAdd(U32(b, e), (U32(b, e + 4), U32(b, e + 8)));
        }
        if (!streams.TryGetValue(Exception, out var ex))
            return null;

        var modules = new List<(ulong Base, ulong Size, string Name)>();
        if (streams.TryGetValue(ModuleList, out var ml))
        {
            uint n = U32(b, ml.Rva);
            for (uint i = 0; i < n; i++)
            {
                long m = ml.Rva + 4 + i * 108;
                modules.Add((U64(b, m), U32(b, m + 8), Path.GetFileName(Utf16(b, U32(b, m + 20)).Replace('\\', '/'))));
            }
        }
        string? ModuleAt(ulong a) => modules.FirstOrDefault(m => a >= m.Base && a < m.Base + m.Size).Name;

        var memory = new List<(ulong Va, long Offset, ulong Length)>();
        if (streams.TryGetValue(MemoryList, out var mem))
        {
            uint n = U32(b, mem.Rva);
            for (uint i = 0; i < n; i++)
            {
                long d = mem.Rva + 4 + i * 16;
                memory.Add((U64(b, d), U32(b, d + 12), U32(b, d + 8)));
            }
        }
        if (streams.TryGetValue(Memory64List, out var m64))
        {
            ulong n = U64(b, m64.Rva);
            long offset = (long)U64(b, m64.Rva + 8);
            for (ulong i = 0; i < n; i++)
            {
                long d = m64.Rva + 16 + (long)i * 16;
                ulong length = U64(b, d + 8);
                memory.Add((U64(b, d), offset, length));
                offset += (long)length;
            }
        }

        // MINIDUMP_EXCEPTION_STREAM: ThreadId, Alignment, MINIDUMP_EXCEPTION (152 Bytes), ThreadContext (Größe, RVA).
        long record = ex.Rva + 8;
        uint code = U32(b, record);
        ulong address = U64(b, record + 16);
        uint contextRva = U32(b, ex.Rva + 8 + 152 + 4);

        bool is64 = streams.TryGetValue(SystemInfo, out var si) && BitConverter.ToUInt16(b, (int)si.Rva) == 9; // PROCESSOR_ARCHITECTURE_AMD64
        ulong sp = is64 ? U64(b, contextRva + 0x98) : U32(b, contextRva + 0xC4);
        int width = is64 ? 8 : 4;

        var stack = new List<string>();
        for (ulong a = sp; a < sp + StackScanBytes && stack.Count < maxStackModules; a += (ulong)width)
        {
            var region = memory.FirstOrDefault(r => a >= r.Va && a + (ulong)width <= r.Va + r.Length);
            if (region.Length == 0)
                continue;
            long at = region.Offset + (long)(a - region.Va);
            ulong value = is64 ? U64(b, at) : U32(b, at);
            if (ModuleAt(value) is { } name && (stack.Count == 0 || !stack[^1].Equals(name, StringComparison.OrdinalIgnoreCase)))
                stack.Add(name);
        }
        return new CrashDumpInfo(code, address, ModuleAt(address), stack);
    }

    private static uint U32(byte[] b, long o) => BitConverter.ToUInt32(b, checked((int)o));
    private static ulong U64(byte[] b, long o) => BitConverter.ToUInt64(b, checked((int)o));
    private static string Utf16(byte[] b, uint rva) => System.Text.Encoding.Unicode.GetString(b, (int)rva + 4, (int)U32(b, rva));
}
