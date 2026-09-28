using System.Buffers.Binary;
using System.Text;

namespace Dlss5Optimizer.Core.Tests;

/// <summary>Baut minimale, aber gültige PE-Dateien für Tests (kein Windows nötig).</summary>
internal static class TestPe
{
    public const ushort I386 = 0x014C;
    public const ushort Amd64 = 0x8664;

    public static byte[] Build(ushort machine, string[] imports, string[]? delayImports = null, Version? version = null, bool delayUsesVa = false, bool largeAddressAware = false, string[]? exports = null)
    {
        bool pe32Plus = machine != I386;
        delayImports ??= [];
        exports ??= [];
        const uint sectionRva = 0x1000;
        const int sectionFileOffset = 0x200;
        const ulong imageBase = 0x140000000;
        ulong effectiveImageBase = pe32Plus ? imageBase : 0x400000;

        // Abschnittsinhalt: [Import-Deskriptoren][Delay-Deskriptoren][Namen][Ressource]
        var sec = new MemoryStream();
        int importDescSize = (imports.Length + 1) * 20;
        int delayDescSize = delayImports.Length > 0 ? (delayImports.Length + 1) * 32 : 0;
        int namesStart = importDescSize + delayDescSize;

        var names = new MemoryStream();
        var importNameRvas = imports.Select(n => AddName(names, n)).ToArray();
        var delayNameRvas = delayImports.Select(n => AddName(names, n)).ToArray();
        uint AddName(MemoryStream ms, string n)
        {
            uint rva = sectionRva + (uint)namesStart + (uint)ms.Length;
            ms.Write(Encoding.ASCII.GetBytes(n));
            ms.WriteByte(0);
            return rva;
        }

        var buf = new byte[20];
        foreach (var rva in importNameRvas)
        {
            Array.Clear(buf);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), rva);
            sec.Write(buf);
        }
        sec.Write(new byte[20]);

        var dbuf = new byte[32];
        foreach (var rva in delayNameRvas)
        {
            Array.Clear(dbuf);
            BinaryPrimitives.WriteUInt32LittleEndian(dbuf, delayUsesVa ? 0u : 1u);
            ulong nameRef = delayUsesVa ? effectiveImageBase + rva : rva;
            BinaryPrimitives.WriteUInt32LittleEndian(dbuf.AsSpan(4), (uint)nameRef);
            sec.Write(dbuf);
        }
        if (delayImports.Length > 0)
            sec.Write(new byte[32]);

        names.Position = 0;
        names.CopyTo(sec);

        // Export-Verzeichnis (40 Bytes) + Namenszeiger + Namen – nur, was ein Namensvergleich braucht.
        uint exportRva = 0, exportSize = 0;
        if (exports.Length > 0)
        {
            while (sec.Length % 4 != 0)
                sec.WriteByte(0);
            exportRva = sectionRva + (uint)sec.Length;
            uint pointersRva = exportRva + 40;
            uint namesRva = pointersRva + (uint)exports.Length * 4;
            var dir = new byte[40];
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(24), (uint)exports.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(32), pointersRva);
            sec.Write(dir);
            var exportNames = new MemoryStream();
            var ptr = new byte[4];
            foreach (var name in exports)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ptr, namesRva + (uint)exportNames.Length);
                sec.Write(ptr);
                exportNames.Write(Encoding.ASCII.GetBytes(name));
                exportNames.WriteByte(0);
            }
            exportNames.Position = 0;
            exportNames.CopyTo(sec);
            exportSize = (uint)(sectionRva + sec.Length - exportRva);
        }

        uint resourceRva = 0, resourceSize = 0;
        if (version is not null)
        {
            while (sec.Length % 4 != 0)
                sec.WriteByte(0);
            resourceRva = sectionRva + (uint)sec.Length;
            var res = new byte[64];
            BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(8), 0xFEEF04BD);
            BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(12), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(16), (uint)((version.Major << 16) | version.Minor));
            BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(20), (uint)((version.Build << 16) | version.Revision));
            sec.Write(res);
            resourceSize = (uint)res.Length;
        }
        var section = sec.ToArray();

        ushort optSize = (ushort)(pe32Plus ? 240 : 224);
        var file = new byte[sectionFileOffset + section.Length];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x3C), 0x40);
        int pe = 0x40;
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(file, pe);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 4), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 20), optSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(pe + 22), (ushort)(0x0002 | (largeAddressAware ? 0x0020 : 0)));

        int opt = pe + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(opt), (ushort)(pe32Plus ? 0x20B : 0x10B));
        if (pe32Plus)
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(opt + 24), effectiveImageBase);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + 28), (uint)effectiveImageBase);
        int rvaCount = pe32Plus ? 108 : 92;
        int dirs = pe32Plus ? 112 : 96;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + rvaCount), 16);
        void Dir(int index, uint rva, uint size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirs + index * 8), rva);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirs + index * 8 + 4), size);
        }
        Dir(0, exportRva, exportSize);
        Dir(1, sectionRva, (uint)importDescSize);
        Dir(2, resourceRva, resourceSize);
        if (delayImports.Length > 0)
            Dir(13, sectionRva + (uint)importDescSize, (uint)delayDescSize);

        int sh = opt + optSize;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(file, sh);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sh + 8), (uint)section.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sh + 12), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sh + 16), (uint)section.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sh + 20), sectionFileOffset);

        section.CopyTo(file, sectionFileOffset);
        return file;
    }

    public static string Write(string path, ushort machine, string[] imports, string[]? delayImports = null, Version? version = null, byte[]? trailer = null, bool largeAddressAware = false, string[]? exports = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Build(machine, imports, delayImports, version, largeAddressAware: largeAddressAware, exports: exports);
        if (trailer is not null)
            bytes = [.. bytes, .. trailer];
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>Temporärer Ordner, der nach dem Test gelöscht wird.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dlss5opt-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content = "")
    {
        var p = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Dir(string relative)
    {
        var p = Combine(relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
