#pragma once

namespace NAM::Voices
{
    /// NPCs a pick made the other sex keep their own voice on their record, so the game picks the same lines for
    /// them; each line they say is then played from the new voice's recording of it. Without such a recording the
    /// line plays from the old voice's own file with no volume, so the mouth moves as always and nothing is heard.
    /// Other NPCs' lines are left alone.
    /// Hooks where the game makes a line's sound path; installed when the plugin loads.
    void Install();

    /// The NPCs whose lines play in another voice, and that voice.
    void Set(std::unordered_map<RE::TESNPC*, RE::BGSVoiceType*> a_speaksAs);
}
