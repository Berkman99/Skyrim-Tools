using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Mugshots;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.App;

/// <summary>Creates the built-in 3D renderer used for generated pictures.</summary>
public static class InternalRendererFactory
{
#if INTERNAL_RENDERER
    public const bool IsAvailable = true;
    private static InternalRendererGenerator? _instance;

    /// <summary>Must be called on the UI thread: the renderer's OpenGL window has to live on the main thread.</summary>
    public static IMugshotGenerator? TryCreate(GameSetup setup, PluginCache plugins)
    {
        try
        {
            _instance?.Retarget(setup, plugins);
            return _instance ??= new InternalRendererGenerator(setup, plugins);
        }
        catch (Exception ex)
        {
            MugshotService.Log("Internal renderer failed to start: " + ex);
            return null;
        }
    }
#else
    public const bool IsAvailable = false;

    public static IMugshotGenerator? TryCreate(GameSetup setup, PluginCache plugins) => null;
#endif
}
