using System.Collections.Concurrent;
using Mutagen.Bethesda.Skyrim;
using NpcAppearanceManager.Core.Environment;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>Opens plugins read-only on demand and keeps them open until disposed.</summary>
public sealed class PluginCache : IDisposable
{
    private readonly GameSetup _setup;
    private readonly ConcurrentDictionary<string, ISkyrimModDisposableGetter?> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public PluginCache(GameSetup setup) => _setup = setup;

    /// <summary>Opens the winning copy of a plugin by file name (mods, then the game Data folder).</summary>
    public ISkyrimModGetter? Get(string pluginFileName)
    {
        var path = _setup.ResolvePluginPath(pluginFileName);
        return path == null ? null : GetByPath(path);
    }

    public ISkyrimModGetter? GetByPath(string path) =>
        _byPath.GetOrAdd(path, p =>
        {
            try { return SkyrimMod.CreateFromBinaryOverlay(p, GameSetup.Release); }
            catch { return null; }
        });

    public void Dispose()
    {
        foreach (var mod in _byPath.Values) mod?.Dispose();
        _byPath.Clear();
    }
}
