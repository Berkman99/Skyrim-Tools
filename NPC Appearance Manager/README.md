# NPC Appearance Manager

A small tool for Skyrim Special Edition. You pick which NPC appearance overhauls to use, choose a look for each NPC (with pictures), and it builds **one new mod with one plugin** that contains everything those looks need. Afterwards you can switch the original overhauls off.

It only reads the mods you tick. Everything you change is saved immediately, so the tool remembers it next time. The window is always dark (deep teal with amber highlights).

## Using it

1. **Add it to Mod Organizer 2.** In MO2: *Executables* (the gears icon) → **+** → *Add from file* → pick `NPC Appearance Manager.exe`. No arguments are needed. Start it from MO2 and it finds your game and the active profile by itself.
   - Without MO2 it looks for Skyrim automatically, or you set the folder once in *Settings*.
2. **Pick overhauls.** Your mods are listed in the same order as in MO2, under MO2's separators. Click a separator to fold it. NPC overhauls carry a badge: **facegen + plugin**, or **facegen** for overhauls without a plugin (like Nordic Faces). Other mods have no badge. Disabled mods are hidden unless you tick **Show disabled mods**. Nothing is ticked for you, except that the mods you ticked last time are ticked again. Press **Load**.
   - **Merge dependencies** (on by default): under each ticked mod, the mods that need it are listed with their own checkboxes, and they get ticked too. A dependency is a whole mod, never a single plugin. Mods with too many masters (more than 254, for example DynDOLOD) are shown in red and aren't ticked automatically; you can still tick them. Hover to see the master count.
3. **Pick looks.** The NPC list is on the left, with search and filters above it; the pictures of each available look are on the right. Only NPCs with more than one look are shown unless you tick "Show NPCs with only one look". Pictures load in the background as soon as the list opens. Use the size slider to make them bigger or smaller. Click a picture to choose it. Right-click a picture to use that mod for all of its NPCs, or to link a mugshot folder whose name differs from the mod's.
   - **The original look** is the tile named after where the NPC comes from: **Skyrim** (Skyrim, Update and the DLCs), **Creation Club**, or the mod that adds the NPC. If that mod is one of the ticked ones, its own tile is marked "original" instead. Picking the original leaves the NPC out of the new plugin.
   - **Mugshots of mods that aren't loaded** show as normal tiles too, marked "Not installed" or "Not loaded" in red. They can't be picked.
4. **Build.** This writes the new mod into your MO2 `mods` folder (default name "NAM Merge", plugin "NAM Merge.esp"; type the plugin name without ".esp").
   - **Disable merged mods after build** (on by default) disables them in your MO2 profile and enables the new mod. If MO2 is open, press F5 in MO2 after closing the tool. Backups are made first and show up in MO2's *Restore backup* lists as `NAM 2026-10-22 14.30.00` (date, then time).

### Where the original look comes from

Every NPC's ID belongs to the plugin that adds it, so the source is exact: Skyrim's own plugins and archives, a Creation Club plugin, or the mod that adds the NPC. Overhauls never count as the original, ticked or not. In the build, an NPC set to its original is left out of the new plugin entirely, so with the merged mods switched off the game shows it without them.

### What gets merged

- **Ticked mods are merged whole**, enabled or disabled: every record and every file (archives are unpacked). That includes mods that add new NPCs; they get new IDs in the new plugin and their faces move with them. A disabled mod's plugins are placed in the load order right after their masters, and its own dependency list (including disabled patches) shows under it.
- If a merged plugin needs a plugin that's neither enabled nor ticked, the build stops and names it.
- **For each NPC**, the tool starts from the record exactly as your game currently sees it (so AI, voice and stat edits from every mod are kept) and swaps in only the chosen look.

### When a mod stays on

- **A dependency you left unticked still needs it.** The game wouldn't start without it, so it stays on. The results show which mod needs it when you hover.
- **Too many masters.** If everything ticked needs more than 254 masters together, the build stops and names the mods with the most masters.

Mods you didn't tick are never merged or switched off. If a look uses something from another mod (a hair pack, for example), the new plugin just points to it. A face can also need other mods to work in game: Nordic Faces' physics hair, for example, needs Faster HDT-SMP, a skeleton and Vanilla Hair Remake SMP.

Building again later starts from the original mods: whatever the last build switched off is treated as switched on, and anything no longer merged gets switched back on.

## Pictures

Each look's picture is found in this order:

1. **Your mugshot folder** (*Settings*), in the same layout NPC Plugin Chooser 2 and EasyNPC use: `Mod name\Plugin.esp\00012345.png`. Original looks use the folder named after their source: the mod's name, `Skyrim` or `Creation Club`. If NPC Plugin Chooser 2 is in your MO2 executables, its `Mugshots` folder is used automatically.
2. **FaceFinder** (npcfacefinder.com). This is built in but grayed out until the shared FaceFinder key is added (see below).
3. **Generated**, if switched on in *Settings*:
   - **Internal renderer**: NPC Plugin Chooser 2's 3D renderer. Needs a graphics card.
   - **Portrait Creator program**: the external `NPCPortraitCreator.exe`. Its path is filled in automatically when NPC2 is found.

Downloaded and generated pictures are cached in `Mugshot Cache` next to the program. Problems with pictures are written to `Mugshot Cache\mugshots.log`.

## For developers

- Requirements: .NET 10 SDK, Windows.
- Build: `dotnet build NpcFaceManager.sln`
- Tests: `dotnet test tests\NpcFaceManager.Tests`
- Projects (the code still uses the old internal name `NpcFaceManager`):
  - `src\NpcFaceManager.Core`: MO2/game detection, scanning, original-look detection, the build step and mugshot lookup. No UI.
  - `src\NpcFaceManager.App`: the WPF window.
  - `tests\NpcFaceManager.Tests`: unit tests that don't need the game.
- Settings are saved as `NpcAppearanceManager.settings.json` next to the program; an older `NpcFaceManager.settings.json` is read once if there's no new one. Output folders carry `NpcAppearanceManager.output.json`; folders with the older `NpcFaceManager.output.json` are still recognized.
- **FaceFinder key:** put it in `FaceFinderClient.ApiKey` (`src\NpcFaceManager.Core\Mugshots\FaceFinderClient.cs`). The option enables itself once the key is non-empty.
- **Internal renderer:** NPC2's renderer (`CharacterViewer.Rendering.dll`) isn't on nuget.org, so the app project takes it, and the libraries it needs, from an installed NPC Plugin Chooser 2. The folder defaults to `D:\Other\Gaming\Modding\Tools\NPC Plugin Chooser`; override it with `dotnet build -p:Npc2Folder="C:\path\to\NPC2"`. If the folder isn't found the app still builds and the option shows as unavailable.
- Launch arguments, for testing outside MO2: `--mo2 "path\to\ModOrganizer.ini" --profile "Profile name"`.
- `..\NPCPluginChooser2 Repo\` (next to this folder) is the NPC2 source, kept locally for reference only. It isn't part of the solution and isn't in the repository.
