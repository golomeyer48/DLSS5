using System.Buffers.Binary;
using System.Text;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Detection;

/// <summary>
/// Minimaler, plattformunabhängiger PE-Leser (EXE/DLL): Bitness, statische und verzögerte Imports,
/// numerische Dateiversion. Liest nur Header und die nötigen Tabellen, nie die ganze Datei.
/// </summary>
public sealed class PeFile
{
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;
    private const ushort MagicPe32 = 0x10B;
    private const ushort MagicPe32Plus = 0x20B;
    private const int DirImport = 1;
    private const int DirResource = 2;
    private const int DirDelayImport = 13;
    private const uint FixedFileInfoSignature = 0xFEEF04BD;
    private const ushort LargeAddressAwareFlag = 0x0020;

    private readonly Section[] _sections;

    public Bitness Bitness { get; }
    public ulong ImageBase { get; }
    public IReadOnlyList<string> Imports { get; }
    public IReadOnlyList<string> DelayImports { get; }
    public Version? FileVersion { get; }

    /// <summary>
    /// 32-Bit-Programme ohne dieses Flag bekommen nur 2 GB Adressraum. Mit ReShade, DXVK und dem
    /// Feeder im Prozess reicht das bei alten, gemoddeten Spielen oft nicht (4GB-Patch).
    /// </summary>
    public bool LargeAddressAware { get; private init; }

    /// <summary>Alle importierten DLL-Namen (statisch + verzögert), kleingeschrieben.</summary>
    public IEnumerable<string> AllImports => Imports.Concat(DelayImports);

    private PeFile(Bitness bitness, ulong imageBase, Section[] sections, List<string> imports, List<string> delayImports, Version? version)
    {
        Bitness = bitness;
        ImageBase = imageBase;
        _sections = sections;
        Imports = imports;
        DelayImports = delayImports;
        FileVersion = version;
    }

    public static PeFile? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryRead(fs);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PeFile? TryRead(Stream stream)
    {
        try
        {
            return Read(stream);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or IOException)
        {
            return null;
        }
    }

    private static PeFile Read(Stream s)
    {
        var dos = ReadAt(s, 0, 64);
        if (dos[0] != 'M' || dos[1] != 'Z')
            throw new InvalidDataException("Kein MZ-Header");
        long peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        if (peOffset <= 0 || peOffset > s.Length - 24)
            throw new InvalidDataException("Ungültiger PE-Offset");

        var coff = ReadAt(s, peOffset, 24);
        if (coff[0] != 'P' || coff[1] != 'E' || coff[2] != 0 || coff[3] != 0)
            throw new InvalidDataException("Keine PE-Signatur");
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(4));
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
        ushort optSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(22));

        var opt = ReadAt(s, peOffset + 24, optSize);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(opt);
        bool pe32Plus = magic switch
        {
            MagicPe32 => false,
            MagicPe32Plus => true,
            _ => throw new InvalidDataException("Unbekannter Optional-Header"),
        };
        ulong imageBase = pe32Plus
            ? BinaryPrimitives.ReadUInt64LittleEndian(opt.AsSpan(24))
            : BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(28));
        int rvaCountOffset = pe32Plus ? 108 : 92;
        int dirOffset = pe32Plus ? 112 : 96;
        uint dirCount = optSize >= rvaCountOffset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(rvaCountOffset)) : 0;

        (uint Rva, uint Size) Dir(int index)
        {
            int off = dirOffset + index * 8;
            if (index >= dirCount || off + 8 > opt.Length)
                return (0, 0);
            return (BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(off)), BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(off + 4)));
        }

        var secBytes = ReadAt(s, peOffset + 24 + optSize, sectionCount * 40);
        var sections = new Section[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            var span = secBytes.AsSpan(i * 40, 40);
            sections[i] = new Section(
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(span[16..]),
                RawPointer: BinaryPrimitives.ReadUInt32LittleEndian(span[20..]));
        }

        var bitness = machine switch
        {
            MachineI386 => Bitness.X86,
            MachineAmd64 => Bitness.X64,
            MachineArm64 => Bitness.Arm64,
            _ => Bitness.Unknown,
        };

        var pe = new PeFile(bitness, imageBase, sections, [], [], null);
        var imports = pe.ReadImports(s, Dir(DirImport).Rva);
        var delay = pe.ReadDelayImports(s, Dir(DirDelayImport).Rva);
        var version = pe.ReadFixedFileVersion(s, Dir(DirResource));
        return new PeFile(bitness, imageBase, sections, imports, delay, version)
        {
            LargeAddressAware = (characteristics & LargeAddressAwareFlag) != 0,
        };
    }

    private List<string> ReadImports(Stream s, uint rva)
    {
        var result = new List<string>();
        if (rva == 0)
            return result;
        long? off = RvaToOffset(rva);
        if (off is null)
            return result;

        // IMAGE_IMPORT_DESCRIPTOR: 20 Bytes, Name-RVA an Offset 12, Ende = Nulleintrag.
        for (int i = 0; i < 4096; i++)
        {
            var desc = ReadAt(s, off.Value + i * 20L, 20);
            if (desc.All(b => b == 0))
                break;
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(12));
            var name = ReadAsciiZ(s, nameRva);
            if (!string.IsNullOrEmpty(name))
                result.Add(name.ToLowerInvariant());
        }
        return result;
    }

    private List<string> ReadDelayImports(Stream s, uint rva)
    {
        var result = new List<string>();
        if (rva == 0)
            return result;
        long? off = RvaToOffset(rva);
        if (off is null)
            return result;

        // IMAGE_DELAYLOAD_DESCRIPTOR: 32 Bytes. Attribut-Bit 0 = RVAs, sonst (alte VC6-Form) VAs.
        for (int i = 0; i < 4096; i++)
        {
            var desc = ReadAt(s, off.Value + i * 32L, 32);
            if (desc.All(b => b == 0))
                break;
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(desc);
            ulong nameRef = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(4));
            if (nameRef == 0)
                break;
            uint nameRva = (attributes & 1) != 0 ? (uint)nameRef : (uint)(nameRef - ImageBase);
            var name = ReadAsciiZ(s, nameRva);
            if (!string.IsNullOrEmpty(name))
                result.Add(name.ToLowerInvariant());
        }
        return result;
    }

    /// <summary>
    /// Sucht VS_FIXEDFILEINFO (Signatur 0xFEEF04BD) im Ressourcen-Bereich. Robuster und viel kürzer
    /// als ein vollständiger Ressourcenbaum-Parser, und für Versionsvergleiche ausreichend.
    /// </summary>
    private Version? ReadFixedFileVersion(Stream s, (uint Rva, uint Size) dir)
    {
        if (dir.Rva == 0 || dir.Size == 0)
            return null;
        long? off = RvaToOffset(dir.Rva);
        if (off is null)
            return null;
        int size = (int)Math.Min(dir.Size, 16 * 1024 * 1024);
        size = (int)Math.Min(size, s.Length - off.Value);
        if (size <= 0)
            return null;
        var data = ReadAt(s, off.Value, size);
        Span<byte> sig = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sig, FixedFileInfoSignature);
        int idx = data.AsSpan().IndexOf(sig);
        if (idx < 0 || idx + 16 > data.Length)
            return null;
        uint ms = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idx + 8));
        uint ls = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idx + 12));
        return new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
    }

    private long? RvaToOffset(uint rva)
    {
        foreach (var sec in _sections)
        {
            uint span = Math.Max(sec.VirtualSize, sec.RawSize);
            if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + span)
            {
                uint delta = rva - sec.VirtualAddress;
                if (delta >= sec.RawSize)
                    return null;
                return (long)sec.RawPointer + delta;
            }
        }
        return null;
    }

    private string? ReadAsciiZ(Stream s, uint rva)
    {
        long? off = RvaToOffset(rva);
        if (off is null || off.Value >= s.Length)
            return null;
        int max = (int)Math.Min(260, s.Length - off.Value);
        var buf = ReadAt(s, off.Value, max);
        int end = Array.IndexOf(buf, (byte)0);
        if (end < 0)
            end = buf.Length;
        return Encoding.ASCII.GetString(buf, 0, end);
    }

    private static byte[] ReadAt(Stream s, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > s.Length)
            throw new EndOfStreamException();
        var buf = new byte[count];
        s.Position = offset;
        s.ReadExactly(buf);
        return buf;
    }

    private readonly record struct Section(uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer);
}
