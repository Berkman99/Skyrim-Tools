using System.Text;
using System.Text.RegularExpressions;

namespace NpcAppearanceManager.Core.Environment;

/// <summary>
/// One Mod Organizer 2 instance, as described by its ModOrganizer.ini.
/// </summary>
public sealed class Mo2Instance
{
    public required string IniPath { get; init; }
    public required string BaseDirectory { get; init; }
    public required string ModsDirectory { get; init; }
    public required string ProfilesDirectory { get; init; }
    public required string OverwriteDirectory { get; init; }
    public string? GamePath { get; init; }
    public string? SelectedProfile { get; init; }

    /// <summary>Folder that contains ModOrganizer.exe, when known.</summary>
    public string? ExecutableDirectory { get; init; }

    public string ProfileDirectory(string profile) => Path.Combine(ProfilesDirectory, profile);


    public IReadOnlyList<string> ListProfiles() =>
        Directory.Exists(ProfilesDirectory)
            ? Directory.GetDirectories(ProfilesDirectory).Select(Path.GetFileName).OfType<string>().OrderBy(x => x).ToList()
            : [];

    public static Mo2Instance Load(string iniPath, string? executableDirectory = null)
    {
        var ini = IniFile.Read(iniPath);
        var iniDir = Path.GetDirectoryName(Path.GetFullPath(iniPath))!;

        var baseDir = ExpandPath(ini.Get("Settings", "base_directory"), iniDir, iniDir) ?? iniDir;
        string Dir(string key, string fallbackName) =>
            ExpandPath(ini.Get("Settings", key), baseDir, iniDir) ?? Path.Combine(baseDir, fallbackName);

        return new Mo2Instance
        {
            IniPath = Path.GetFullPath(iniPath),
            BaseDirectory = baseDir,
            ModsDirectory = Dir("mod_directory", "mods"),
            ProfilesDirectory = Dir("profiles_directory", "profiles"),
            OverwriteDirectory = Dir("overwrite_directory", "overwrite"),
            GamePath = ExpandPath(ini.Get("General", "gamePath"), baseDir, iniDir),
            SelectedProfile = ini.Get("General", "selected_profile"),
            ExecutableDirectory = executableDirectory,
        };
    }

    private static string? ExpandPath(string? raw, string baseDir, string iniDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase)
                       .Replace('/', '\\');
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(iniDir, value));
    }
}

/// <summary>
/// Minimal reader for the Qt-style INI files MO2 writes (handles @ByteArray(...) and escaped backslashes).
/// </summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> GetSection(string section) =>
        _sections.TryGetValue(section, out var s) ? s : new Dictionary<string, string>();

    public static IniFile Read(string path)
    {
        var ini = new IniFile();
        var current = ini.Section("General");
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = ini.Section(line[1..^1]);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[line[..eq].Trim()] = Unquote(line[(eq + 1)..].Trim());
        }
        return ini;
    }

    private Dictionary<string, string> Section(string name)
    {
        if (!_sections.TryGetValue(name, out var s))
        {
            s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[name] = s;
        }
        return s;
    }

    private static readonly Regex ByteArray = new(@"^@ByteArray\((.*)\)$", RegexOptions.Compiled);

    internal static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        var m = ByteArray.Match(value);
        if (m.Success) value = m.Groups[1].Value;
        return value.Replace(@"\\", @"\");
    }
}
