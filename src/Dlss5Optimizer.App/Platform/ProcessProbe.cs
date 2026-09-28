using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dlss5Optimizer.App.Platform;

/// <summary>
/// Liest die geladenen Module eines laufenden Spiels. EnumProcessModulesEx mit LIST_MODULES_ALL,
/// weil Process.Modules bei 32-Bit-Spielen aus einem 64-Bit-Prozess nur die WOW64-Module liefert.
/// </summary>
public static class ProcessProbe
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint LIST_MODULES_ALL = 0x03;

    public static IReadOnlyList<string> LoadedModules(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Prozess kann nicht geöffnet werden");
        try
        {
            var modules = new IntPtr[1024];
            int size = IntPtr.Size * modules.Length;
            if (!EnumProcessModulesEx(handle, modules, size, out int needed, LIST_MODULES_ALL))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (needed > size)
            {
                modules = new IntPtr[needed / IntPtr.Size];
                if (!EnumProcessModulesEx(handle, modules, needed, out needed, LIST_MODULES_ALL))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var result = new List<string>();
            var sb = new StringBuilder(1024);
            for (int i = 0; i < needed / IntPtr.Size; i++)
            {
                sb.Clear();
                if (GetModuleFileNameEx(handle, modules[i], sb, sb.Capacity) > 0)
                    result.Add(sb.ToString());
            }
            return result;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Wartet auf den Spielprozess (Launcher starten oft einen anderen Prozess als den gestarteten).
    /// </summary>
    public static async Task<Process?> WaitForProcessAsync(string exePath, TimeSpan timeout, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var p = Process.GetProcessesByName(name).FirstOrDefault();
            if (p is not null)
                return p;
            await Task.Delay(500, ct);
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int cb, out int needed, uint filter);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder name, int size);
}
