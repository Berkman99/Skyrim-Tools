using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NpcAppearanceManager.Core.Environment;

/// <summary>
/// Works out which MO2 instance (if any) launched this program, without asking the user.
/// Order: explicit command-line arguments, then the parent process (ModOrganizer.exe).
/// </summary>
public static class Mo2Detector
{
    public sealed record Detection(Mo2Instance Instance, string? Profile, string HowFound);

    public static Detection? Detect(IReadOnlyList<string> args)
    {
        var fromArgs = FromArguments(args);
        if (fromArgs != null) return fromArgs;

        var mo2Exe = FindAncestor("ModOrganizer.exe");
        if (mo2Exe != null)
        {
            var exeDir = Path.GetDirectoryName(mo2Exe)!;
            var ini = FindIniForExecutable(exeDir);
            if (ini != null)
            {
                var instance = Mo2Instance.Load(ini, exeDir);
                return new Detection(instance, instance.SelectedProfile, "Launched from Mod Organizer 2");
            }
        }
        return null;
    }

    /// <summary>Supports: --mo2 "path\to\ModOrganizer.ini" [--profile "Name"]</summary>
    private static Detection? FromArguments(IReadOnlyList<string> args)
    {
        string? ini = null, profile = null;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals("--mo2", StringComparison.OrdinalIgnoreCase)) ini = args[i + 1];
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase)) profile = args[i + 1];
        }
        if (ini == null) return null;
        if (Directory.Exists(ini)) ini = Path.Combine(ini, "ModOrganizer.ini");
        if (!File.Exists(ini)) return null;
        var instance = Mo2Instance.Load(ini);
        return new Detection(instance, profile ?? instance.SelectedProfile, "Command-line argument");
    }

    /// <summary>
    /// Finds ModOrganizer.ini for a given ModOrganizer.exe folder: portable instance first,
    /// then the global instance currently selected in MO2's registry settings.
    /// </summary>
    public static string? FindIniForExecutable(string exeDir)
    {
        var portableIni = Path.Combine(exeDir, "ModOrganizer.ini");
        if (File.Exists(Path.Combine(exeDir, "portable.txt")) && File.Exists(portableIni)) return portableIni;

        var current = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Mod Organizer Team\Mod Organizer", "CurrentInstance", null) as string;
        var instancesRoot = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (!string.IsNullOrEmpty(current))
        {
            var globalIni = Path.Combine(instancesRoot, current, "ModOrganizer.ini");
            if (File.Exists(globalIni)) return globalIni;
        }

        return File.Exists(portableIni) ? portableIni : null;
    }

    /// <summary>Whether this program was opened from Vortex (as one of its tools).</summary>
    public static bool LaunchedFromVortex() => FindAncestor("Vortex.exe") != null;

    /// <summary>Walks up the parent-process chain looking for a program (e.g. ModOrganizer.exe).</summary>
    private static string? FindAncestor(string exeName)
    {
        try
        {
            int pid = System.Environment.ProcessId;
            for (int depth = 0; depth < 5; depth++)
            {
                int parent = GetParentProcessId(pid);
                if (parent <= 0) return null;
                var path = GetProcessPath(parent);
                if (path != null && Path.GetFileName(path).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    return path;
                pid = parent;
            }
        }
        catch
        {
            // Detection is best-effort; the caller falls back to other methods.
        }
        return null;
    }

    public static bool IsMo2Running(Mo2Instance instance)
    {
        foreach (var p in Process.GetProcessesByName("ModOrganizer"))
        {
            var path = GetProcessPath(p.Id);
            if (path == null || instance.ExecutableDirectory == null) return true;
            if (string.Equals(Path.GetDirectoryName(path), instance.ExecutableDirectory, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref ProcessBasicInformation info, int size, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, System.Text.StringBuilder name, ref int size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static int GetParentProcessId(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return -1;
        try
        {
            var info = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out _);
            return status == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : -1;
        }
        finally { CloseHandle(handle); }
    }

    private static string? GetProcessPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
}
