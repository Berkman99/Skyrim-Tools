#pragma once

namespace NAM::Picks
{
    /// When the game has loaded its plugins, reads the picks list NAM's Save wrote and gives each picked NPC its new
    /// face. Plugins set NPCs' looks every time the game starts, so this runs every time too, and again after each
    /// save is loaded (a save can carry an NPC's old values), redrawing NPCs already in view.
    void Start();
}
