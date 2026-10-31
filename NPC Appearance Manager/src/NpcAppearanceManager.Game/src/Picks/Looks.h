#pragma once

namespace NAM::Looks
{
    /// One picked NPC: the values of the record that goes with its new face, as NAM's Save wrote them. Values the
    /// list leaves out keep what the NPC has.
    struct Look
    {
        RE::TESNPC*                   npc = nullptr;
        bool                          setHeadParts = false;
        std::vector<RE::BGSHeadPart*> headParts;
        RE::TESObjectARMO*            skin = nullptr;
        bool                          raceSkin = false;  // "none": no body of its own, so the race's
        RE::BGSColorForm*             hairColor = nullptr;
        RE::BGSTextureSet*            headTexture = nullptr;
        RE::TESRace*                  race = nullptr;
        RE::BGSVoiceType*             voice = nullptr;
        RE::BGSVoiceType*             speaksAs = nullptr;  // the voice its lines play in; its record keeps its own
        std::optional<float>          height;
        std::optional<float>          weight;
        std::optional<bool>           female;
        std::optional<RE::Color>      bodyTint;
        std::optional<std::string>    name;  // as the game stores names (Windows-1252)

        /// The pick makes the NPC the other sex (decided when the list is read, before any value is applied).
        bool sexChanged = false;
    };

    /// The [[looks]] of the picks list, for NPCs that are in the game.
    std::vector<Look> Load(const toml::table& a_list);

    /// Gives the NPC its picked values.
    void Apply(const Look& a_look);
}
