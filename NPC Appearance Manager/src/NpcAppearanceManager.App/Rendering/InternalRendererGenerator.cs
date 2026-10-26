#if INTERNAL_RENDERER
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CharacterViewer.Rendering;
using CharacterViewer.Rendering.Offscreen;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Mugshots;
using NpcAppearanceManager.Core.Scanning;
using NpcAppearanceManager.Core.Settings;
using ReactiveUI;
using ReactiveUI.Builder;
using RenderSex = CharacterViewer.Rendering.Sex;

namespace NpcAppearanceManager.App;

/// <summary>
/// Generates pictures with NPC Plugin Chooser 2's offscreen renderer. Only the face is resolved here
/// (head, face tint, skin/body, hair colour); outfits, wigs and the rest of NPC2's extras are skipped.
/// </summary>
internal sealed class InternalRendererGenerator : IMugshotGenerator
{
    private readonly IOffscreenRenderer _renderer;
    private readonly DataFolderAdapter _dataFolder = new();
    private readonly BsaAdapter _bsa = new();
    private readonly SemaphoreSlim _slots = new(2);
    private GameSetup _setup;
    private PluginCache _plugins;
    private Task<ILinkCache<ISkyrimMod, ISkyrimModGetter>>? _linkCache;

    public string Name => "Internal renderer";

    private static bool _reactiveUiReady;

    public InternalRendererGenerator(GameSetup setup, PluginCache plugins)
    {
        // The renderer uses ReactiveUI internally, which (since v20) must be set up once before first use.
        if (!_reactiveUiReady)
        {
            RxAppBuilder.CreateReactiveUIBuilder().WithWpf().BuildApp();
            _reactiveUiReady = true;
        }

        _setup = setup;
        _plugins = plugins;
        Retarget(setup, plugins);

        var logger = new NullLogger();
        var gate = new CharacterViewerLogGate();
        var settings = new RenderSettings();
        var assets = new GameAssetResolver(_dataFolder, _bsa, gate, logger);
        var cache = new CharacterPreviewCache(new NoMeshDataSource(), assets, logger, gate, settings);
        _renderer = OffscreenRendererFactory.Create(cache, new BodySlideDeformer(logger, gate), new BsdFileParser(logger, gate),
            new BodyTriFileParser(logger), assets, settings, gate, logger);
    }

    public void Retarget(GameSetup setup, PluginCache plugins)
    {
        _setup = setup;
        _plugins = plugins;
        _linkCache = null;
        _dataFolder.Setup = setup;
        _bsa.Setup = setup;
        _renderer?.InvalidateCaches();
    }

    public async Task<bool> GenerateAsync(NpcLook look, string outputPng, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        var temp = Path.Combine(Path.GetTempPath(), "NPC Appearance Manager", Guid.NewGuid().ToString("N"));
        try
        {
            return await Task.Run(async () =>
            {
                var linkCache = await (_linkCache ??= Task.Run(BuildLinkCache));
                // Face files inside an archive are taken from that exact archive, never another mod's copy.
                var head = look.Faces.NifLoosePath ?? Extract(look.Faces.NifArchive, FaceGenPaths.NifPath(look.Npc), temp) ?? FaceGenPaths.NifPath(look.Npc);
                var tint = look.Faces.DdsLoosePath ?? Extract(look.Faces.DdsArchive, FaceGenPaths.DdsPath(look.Npc), temp) ?? FaceGenPaths.DdsPath(look.Npc);
                var paths = Resolve(look, linkCache, head, tint);
                if (paths == null) return false;

                var request = new OffscreenRenderRequest
                {
                    MeshPaths = paths,
                    Width = 512,
                    Height = 512,
                    BackgroundRgb = (105, 105, 105),
                    Lighting = CharacterViewerLightingPresets.DefaultLayout,
                    Colors = CharacterViewerLightingPresets.DefaultColorScheme,
                    Camera = File.Exists(head) && FaceMeshCheck.HasStrayVertices(head) ? HeadOnlyFraming : PortraitFraming,
                    EnableNormalMapHack = true,
                    UseModdedFallbackTextures = true,
                    AdditionalScopes = BuildScopes(look),
                    AllowLoadOrderFallback = true,
                    VanillaLooseOverridesBsa = true,
                    VanillaLooseOverridesModLoose = false,
                    RenderMissingTextureAsWireframe = false,
                    Cancellation = ct,
                    EnableToneMapping = true,
                    EnableShadows = true,
                    SoftenShadowEdges = true,
                    ShadowPcfRadius = 1.5f,
                    ShadowFrustumRadius = 100f,
                    EnableAmbientOcclusion = true,
                    SsaoRadius = 4.0f,
                    SsaoBias = 0.05f,
                    SsaoIntensity = 1.5f,
                    SsaoThickness = 1.5f,
                    SsaoHairGap = 0.8f,
                    EnableEyeCatchlight = true,
                    SubsurfaceStrength = 1.0f,
                    VignetteRadius = 0.7f,
                    VignetteIntensity = 0.3f,
                    SkinSaturationBoost = 1.0f,
                    Exposure = 1.0f,
                    TonemapHairRelief = true,
                    HairAlbedoCompensate = 1.0f,
                    DaylightBoost = true,
                    DaylightBoostIntensity = 1.1f,
                    EnableBloom = true,
                    BloomIntensity = 0.7f,
                };
                await _renderer.PrewarmAsync(request);
                var png = await _renderer.RenderToPngAsync(request);
                if (png == null || png.Length == 0) return false;
                await File.WriteAllBytesAsync(outputPng, png, ct);
                return true;
            }, ct);
        }
        finally
        {
            _slots.Release();
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Copies one file out of an archive into <paramref name="folder"/>; null if it isn't there.</summary>
    private static string? Extract(string? archive, string relativePath, string folder)
    {
        if (archive == null) return null;
        var entry = ArchiveHelper.Find(archive, relativePath);
        if (entry == null) return null;
        var dest = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using (var src = entry.AsStream())
        using (var dst = File.Create(dest))
            src.CopyTo(dst);
        return dest;
    }

    /// <summary>
    /// Frames the picture around the head mesh (plus hair/accessories above it), the same way NPC2 does by
    /// default: whole head in view, seen from the front, slightly from above.
    /// </summary>
    private static readonly CameraFraming PortraitFraming = new CameraFraming.MeshAware(
        new List<FramingShape>
        {
            new() { Selector = FramingShapeSelector.PrimaryHead.Instance },
            new()
            {
                Selector = FramingShapeSelector.HeadAccessories.Instance,
                Filter = FramingShapeFilter.AboveLowerYOfPrimaryHead.Instance,
                Padding = 0f,
            },
        },
        FrameTopFraction: 1.0f,
        FrameBottomFraction: 0.0f,
        Yaw: 180f,
        Pitch: 4.5f);

    /// <summary>
    /// The same view framed on the head alone, for face meshes with stray vertices (see
    /// <see cref="FaceMeshCheck.HasStrayVertices"/>), which would otherwise stretch the frame and make the head tiny.
    /// </summary>
    private static readonly CameraFraming HeadOnlyFraming = new CameraFraming.MeshAware(
        new List<FramingShape> { new() { Selector = FramingShapeSelector.PrimaryHead.Instance } },
        FrameTopFraction: 1.0f,
        FrameBottomFraction: 0.0f,
        Yaw: 180f,
        Pitch: 4.5f);

    private ILinkCache<ISkyrimMod, ISkyrimModGetter> BuildLinkCache()
    {
        var mods = _setup.LoadOrder.Select(k => _plugins.Get(k.FileName)).OfType<ISkyrimModGetter>().ToList();
        var cache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        _dataFolder.Token = cache;
        return cache;
    }

    /// <summary>Vanilla data first, then the look's own mod folder (the renderer searches last-to-first).</summary>
    private IReadOnlyList<RenderScope> BuildScopes(NpcLook look)
    {
        var vanilla = PluginNames.BaseMasters.Concat(_setup.LoadOrder.Select(k => k.FileName.String)
            .Where(p => p.StartsWith("cc", StringComparison.OrdinalIgnoreCase) && _setup.PluginOwner(p) == null)).ToList();
        var scopes = new List<RenderScope> { new(_setup.DataFolder, vanilla) };
        if (look.Source.Mod.IsGameDataFolder) return scopes;

        // Mods the look builds on: the one that adds the NPC, and the ones the look's plugins list as masters
        // (e.g. a patch's textures usually live in the main mod). Outside MO2's virtual Data folder these
        // files would otherwise be invisible to the renderer.
        var helpers = new List<ModFolder>();
        void AddOwner(string plugin)
        {
            var owner = _setup.PluginOwner(plugin);
            if (owner != null && owner != look.Source.Mod && !helpers.Contains(owner)) helpers.Add(owner);
        }
        AddOwner(look.Npc.ModKey.FileName);
        foreach (var master in look.Source.Plugins.SelectMany(p => p.Masters)) AddOwner(master);
        foreach (var mod in helpers.OrderBy(m => m.Priority))
            scopes.Add(new RenderScope(mod.Path, mod.Plugins.ToList()));

        scopes.Add(new RenderScope(look.Source.Mod.Path, look.Source.Plugins.Select(p => p.FileName).ToList()));
        return scopes;
    }

    private ResolvedNpcMeshPaths? Resolve(NpcLook look, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, string headPath, string tintPath)
    {
        // The look's own plugins win over the load order, so the picture shows that mod's version.
        var modPlugins = look.Source.Plugins
            .Select(p => look.Source.Mod.IsGameDataFolder ? _plugins.Get(p.FileName) : _plugins.GetByPath(Path.Combine(look.Source.Mod.Path, p.FileName)))
            .OfType<ISkyrimModGetter>().Reverse().ToList();

        T? Get<T>(IFormLinkGetter<T> link) where T : class, IMajorRecordGetter
        {
            if (link.IsNull) return null;
            foreach (var mod in modPlugins)
                if (mod.ToUntypedImmutableLinkCache().TryResolve(link.FormKey, typeof(T), out var rec) && rec is T typed) return typed;
            return linkCache.TryResolve<T>(link.FormKey, out var winner) ? winner : null;
        }

        // An original look uses its own source's version of the record (the one its face was made for).
        var npc = look.RecordFrom is { } from
            ? linkCache.ResolveAllContexts<INpc, INpcGetter>(look.Npc).FirstOrDefault(c => from.Contains(c.ModKey))?.Record
            : Get(look.Npc.ToLink<INpcGetter>());
        if (npc == null) return null;
        var sex = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? RenderSex.Female : RenderSex.Male;
        var race = Get(npc.Race);
        var armorRace = race?.ArmorRace.IsNull == false ? race.ArmorRace.FormKey : (FormKey?)null;

        var armor = Get(npc.WornArmor) ?? (race != null ? Get(race.Skin) : null);
        string? body = null, hair = null; // only what shows in a portrait: head, neck/shoulders, hair
        var txst = new Dictionary<string, Dictionary<int, string>>();
        foreach (var armaLink in armor?.Armature ?? [])
        {
            var arma = Get(armaLink);
            if (arma?.BodyTemplate == null) continue;
            var raceKey = race?.FormKey;
            bool raceMatches = raceKey == null || arma.Race.FormKey == raceKey || arma.Race.FormKey == armorRace ||
                               arma.AdditionalRaces.Any(r => r.FormKey == raceKey || r.FormKey == armorRace);
            if (!raceMatches) continue;
            var model = sex == RenderSex.Female ? arma.WorldModel?.Female : arma.WorldModel?.Male;
            var mesh = MeshPath(model?.File?.GivenPath);
            if (mesh == null) continue;
            var flags = arma.BodyTemplate.FirstPersonFlags;
            var skinLink = sex == RenderSex.Female ? arma.SkinTexture?.Female : arma.SkinTexture?.Male;
            var skin = SkinTextures(skinLink == null ? null : Get(skinLink));
            void Take(ref string? slot, string part, BipedObjectFlag flag)
            {
                if (slot != null || !flags.HasFlag(flag)) return;
                slot = mesh;
                if (skin.Count > 0) txst[part] = skin;
            }
            Take(ref body, "Body", BipedObjectFlag.Body);
            Take(ref hair, "Hair", BipedObjectFlag.Hair);
        }

        var skeleton = race?.SkeletalModel == null ? null
            : MeshPath((sex == RenderSex.Female ? race.SkeletalModel.Female : race.SkeletalModel.Male)?.File?.GivenPath);

        (float, float, float)? hairRgb = null;
        if (Get(npc.HairColor) is { } hclr) hairRgb = (hclr.Color.R / 255f, hclr.Color.G / 255f, hclr.Color.B / 255f);
        (float, float, float)? qnam = npc.TextureLighting is { } q ? (q.R / 255f, q.G / 255f, q.B / 255f) : null;

        var eyes = npc.HeadParts.Select(h => Get(h)).OfType<IHeadPartGetter>()
            .Where(h => h.Type == HeadPart.TypeEnum.Eyes && h.EditorID != null).Select(h => h.EditorID!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ResolvedNpcMeshPaths
        {
            BodyMeshPath = Rebase(body, look),
            HairMeshPath = Rebase(hair, look),
            HeadMeshPath = headPath,
            FaceTintPath = tintPath,
            SkeletonPath = Rebase(skeleton, look),
            Sex = sex,
            ResolutionChains = [],
            TxstTextures = txst,
            TextureLightingColor = qnam,
            NpcWeight = Math.Clamp((int)npc.Weight, 0, 100),
            NpcBaseHeight = float.IsFinite(npc.Height) && npc.Height > 0 ? npc.Height : 1f,
            HairColorRgb = hairRgb,
            EyeShapeNames = eyes,
            HideHeadShapeNames = new HashSet<string>(),
        };
    }

    private static string? MeshPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Replace('/', '\\');
        return path.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? path : @"meshes\" + path;
    }

    private static Dictionary<int, string> SkinTextures(ITextureSetGetter? set)
    {
        var slots = new Dictionary<int, string>();
        if (set == null) return slots;
        void Add(int slot, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            slots[slot] = path.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? path : @"textures\" + path;
        }
        Add(0, set.Diffuse?.DataRelativePath.Path);
        Add(1, set.NormalOrGloss?.DataRelativePath.Path);
        Add(2, set.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        Add(5, set.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        Add(3, set.GlowOrDetailMap?.DataRelativePath.Path);
        Add(3, set.Height?.DataRelativePath.Path);
        Add(4, set.Environment?.DataRelativePath.Path);
        Add(6, set.Multilayer?.DataRelativePath.Path);
        Add(7, set.BacklightMaskOrSpecular?.DataRelativePath.Path);
        return slots;
    }

    /// <summary>Uses the look's own loose file when it ships one.</summary>
    private static string? Rebase(string? relative, NpcLook look)
    {
        if (relative == null || look.Source.Mod.IsGameDataFolder) return relative;
        var full = Path.Combine(look.Source.Mod.Path, relative);
        return File.Exists(full) ? full : relative;
    }

    private sealed class DataFolderAdapter : IDataFolderProvider
    {
        public GameSetup? Setup { get; set; }
        public object? Token { get; set; }
        public string DataFolderPath => Setup?.DataFolder ?? "";
        public object CurrentLoadOrderToken => Token ?? this;
    }

    /// <summary>Finds files inside archives: a specific mod's archives, or (as the game does) any active plugin's.</summary>
    private sealed class BsaAdapter : IBsaArchiveProvider
    {
        private readonly ConcurrentDictionary<string, string?> _broadcastHits = new(StringComparer.OrdinalIgnoreCase);
        private List<string>? _loadOrderArchives;
        private GameSetup? _setup;

        public GameSetup? Setup
        {
            get => _setup;
            set { _setup = value; _loadOrderArchives = null; _broadcastHits.Clear(); }
        }

        public void EnsureAllArchivesOpened() { }

        public bool TryLocateInBsa(string subpath, out string containingBsaPath)
        {
            var hit = _broadcastHits.GetOrAdd(subpath, p =>
            {
                foreach (var archive in LoadOrderArchives())
                    if (ArchiveHelper.Find(archive, p) != null) return archive;
                return null;
            });
            containingBsaPath = hit ?? "";
            return hit != null;
        }

        public bool TryLocateInScopedBsa(string subpath, string folderPath, IReadOnlyList<string> modKeyFileNames, out string containingBsaPath)
        {
            containingBsaPath = "";
            if (!Directory.Exists(folderPath)) return false;
            foreach (var archive in Directory.GetFiles(folderPath, "*.bsa"))
            {
                if (!modKeyFileNames.Any(p => ArchiveHelper.IsOwnedBy(Path.GetFileName(archive), p))) continue;
                if (ArchiveHelper.Find(archive, subpath) == null) continue;
                containingBsaPath = archive;
                return true;
            }
            return false;
        }

        public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string error)
        {
            error = "";
            try
            {
                var entry = ArchiveHelper.Find(containingBsaPath, subpath);
                if (entry == null) { error = "not found"; return false; }
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using var src = entry.AsStream();
                using var dst = File.Create(destPath);
                src.CopyTo(dst);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Archives the game loads, latest plugin first.</summary>
        private List<string> LoadOrderArchives()
        {
            if (_loadOrderArchives != null || _setup == null) return _loadOrderArchives ?? [];
            var list = new List<string>();
            foreach (var key in _setup.LoadOrder.Reverse())
            {
                var plugin = key.FileName.String;
                var folder = _setup.PluginOwner(plugin)?.Path ?? _setup.DataFolder;
                if (!Directory.Exists(folder)) continue;
                list.AddRange(Directory.GetFiles(folder, "*.bsa").Where(a => ArchiveHelper.IsOwnedBy(Path.GetFileName(a), plugin)));
            }
            // Vanilla archives that aren't named after a plugin (e.g. "Skyrim - Meshes0.bsa").
            if (Directory.Exists(_setup.DataFolder))
                list.AddRange(Directory.GetFiles(_setup.DataFolder, "Skyrim - *.bsa"));
            return _loadOrderArchives = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private sealed class NoMeshDataSource : INpcMeshDataSource
    {
        public object CurrentInvalidationToken => this;
        public ResolvedNpcMeshPaths? Resolve(NpcIdentity identity) => null;
    }

    private sealed class NullLogger : ICharacterViewerLogger
    {
        private static readonly string LogPath = Path.Combine(AppSettings.CacheFolder, "renderer.log");

        public void LogMessage(string message) { }
        public void LogError(string message) => Write(message);
        public void LogError(string message, Exception ex) => Write(message + " " + ex.Message);
        public IDisposable PushDiagnosticSink(Action<string>? sink) => new Noop();

        private static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.CacheFolder);
                File.AppendAllText(LogPath, $"[{DateTime.Now:u}] {line}{System.Environment.NewLine}");
            }
            catch { }
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>NPC2's default render settings.</summary>
    private sealed class RenderSettings : ICharacterViewerSettings, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string CharacterViewerLightingLayout { get; set; } = "";
        public string CharacterViewerLightingColorScheme { get; set; } = "";
        public IList<CharacterViewerLightingLayout> UserLightingLayouts { get; } = new ObservableCollection<CharacterViewerLightingLayout>();
        public IList<CharacterViewerLightingColorScheme> UserLightingColorSchemes { get; } = new ObservableCollection<CharacterViewerLightingColorScheme>();
        public bool CharacterViewerVerboseLog { get; set; }
        public bool CharacterViewerRenderMissingTextureAsWireframe { get; set; }
        public bool CharacterViewerEnableToneMapping { get; set; } = true;
        public bool CharacterViewerEnableShadows { get; set; } = true;
        public bool CharacterViewerExcludeHairShadowCaster { get; set; }
        public bool CharacterViewerSoftenShadowEdges { get; set; } = true;
        public float CharacterViewerShadowPcfRadius { get; set; } = 1.5f;
        public bool CharacterViewerTightShadowFrustum { get; set; }
        public float CharacterViewerShadowFrustumRadius { get; set; } = 100f;
        public bool CharacterViewerEnableAmbientOcclusion { get; set; } = true;
        public float CharacterViewerSsaoRadius { get; set; } = 4f;
        public float CharacterViewerSsaoBias { get; set; } = 0.05f;
        public float CharacterViewerSsaoIntensity { get; set; } = 1.5f;
        public float CharacterViewerSsaoThickness { get; set; } = 1.5f;
        public float CharacterViewerSsaoHairGap { get; set; } = 0.8f;
        public bool CharacterViewerEnableEyeCatchlight { get; set; } = true;
        public float CharacterViewerSubsurfaceStrength { get; set; } = 1f;
        public float CharacterViewerVignetteRadius { get; set; } = 0.7f;
        public float CharacterViewerVignetteIntensity { get; set; } = 0.3f;
        public float CharacterViewerSkinSaturationBoost { get; set; } = 1f;
        public float CharacterViewerExposure { get; set; } = 1f;
        public bool CharacterViewerTonemapHairRelief { get; set; } = true;
        public float CharacterViewerHairAlbedoCompensate { get; set; } = 1f;
        public bool CharacterViewerDaylightBoost { get; set; } = true;
        public float CharacterViewerDaylightBoostIntensity { get; set; } = 1.1f;
        public bool CharacterViewerEnableBloom { get; set; } = true;
        public float CharacterViewerBloomIntensity { get; set; } = 0.7f;
        // A fixed budget: with "share of free RAM" the cache had too little room for big textures (8K skin and mouth
        // maps of 85 MB each), so they were decoded again for every face, ~11 s each. Half the PC's memory, at most 16 GB.
        public RenderCacheMode CacheMode { get; set; } = RenderCacheMode.FixedRam;
        public long FixedCacheBudgetBytes { get; } =
            Math.Min(16L * 1024 * 1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2);
        public double FreeRamCachePercent => 50;
    }
}
#endif
