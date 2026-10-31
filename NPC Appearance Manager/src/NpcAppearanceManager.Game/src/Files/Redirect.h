#pragma once

namespace NAM::Files::Redirect
{
    /// Hooks where the game asks Windows for a file, so the face files of picked NPCs come straight from the mods
    /// they were picked from. Windows functions only, so nothing here depends on the game version.
    void Install();

    /// The [[files]] of the picks list: each maps a game path (Data-relative, e.g.
    /// meshes\...\facegeom\skyrim.esm\0001327e.nif) to a loose file in a mod's folder, or a file inside its archive.
    void Load(const toml::table& a_list);

    /// Stops serving a listed file (an NPC left as the game has it). Only before <see cref="Start"/>.
    void Drop(std::string_view a_gamePath);

    /// Starts serving the listed files, once the looks that use them are checked.
    void Start();

    /// A listed game path whose file wasn't found (its mod was removed, or renamed and not found by its Nexus ID).
    bool IsMissing(std::string_view a_gamePath);

    /// Opens a few listed paths through the game's own loader and compares them with their sources. Logs PASS/FAIL.
    void SelfCheck();
}
