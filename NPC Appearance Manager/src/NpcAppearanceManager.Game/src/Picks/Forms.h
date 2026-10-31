#pragma once

namespace NAM::Forms
{
    /// "0x0001327E~Skyrim.esm" -> the loaded form, or the one made from NAM's
    /// [[forms]] list when its plugin isn't in the game (a disabled mod).
    RE::TESForm* Lookup(std::string_view a_text);

    template <class T>
    T* LookupAs(std::string_view a_text)
    {
        auto* form = Lookup(a_text);
        return form ? form->As<T>() : nullptr;
    }

    /// Make the head parts, texture sets, hair colours, form lists and skins
    /// that faces from disabled mods use. Forms already in the game are left
    /// alone and used as they are.
    void Load(const toml::table& a_list);
}
