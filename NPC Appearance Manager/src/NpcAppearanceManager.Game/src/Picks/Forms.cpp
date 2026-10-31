#include "PCH.h"

#include "Picks/Forms.h"


namespace NAM::Forms
{
    namespace
    {
        // Forms made here, by lower-case key. Written once at kDataLoaded.
        std::unordered_map<std::string, RE::TESForm*> g_made;

        std::string Lower(std::string_view a_text)
        {
            std::string s{ a_text };
            for (auto& c : s) {
                c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            }
            return s;
        }

        RE::TESForm* LookupLoaded(std::string_view a_text)
        {
            const auto tilde = a_text.find('~');
            if (tilde == std::string_view::npos) {
                return nullptr;
            }
            auto id = std::string{ a_text.substr(0, tilde) };
            if (id.starts_with("0x") || id.starts_with("0X")) {
                id.erase(0, 2);
            }
            std::uint32_t local = 0;
            try {
                local = static_cast<std::uint32_t>(std::stoul(id, nullptr, 16));
            } catch (...) {
                return nullptr;
            }
            const auto plugin = std::string{ a_text.substr(tilde + 1) };
            auto*      handler = RE::TESDataHandler::GetSingleton();
            const auto* file = handler ? handler->LookupModByName(plugin) : nullptr;
            if (!file) {
                return nullptr;
            }
            local &= file->IsLight() ? 0xFFF : 0xFFFFFF;
            return handler->LookupForm(local, plugin);
        }

        template <class T>
        T* Make()
        {
            auto* factory = RE::IFormFactory::GetConcreteFormFactoryByType<T>();
            return factory ? factory->Create() : nullptr;
        }

        RE::TESForm* MakeOfType(std::string_view a_type)
        {
            if (a_type == "HDPT") return Make<RE::BGSHeadPart>();
            if (a_type == "TXST") return Make<RE::BGSTextureSet>();
            if (a_type == "CLFM") return Make<RE::BGSColorForm>();
            if (a_type == "FLST") return Make<RE::BGSListForm>();
            if (a_type == "ARMO") return Make<RE::TESObjectARMO>();
            if (a_type == "ARMA") return Make<RE::TESObjectARMA>();
            return nullptr;
        }

        std::string Text(const toml::table& a_entry, std::string_view a_key)
        {
            return a_entry[a_key].value_or(std::string{});
        }

        template <class T>
        T* Ref(const toml::table& a_entry, std::string_view a_key)
        {
            const auto text = Text(a_entry, a_key);
            return text.empty() ? nullptr : LookupAs<T>(text);
        }

        template <class T, class F>
        void EachRef(const toml::table& a_entry, std::string_view a_key, F&& a_add)
        {
            if (const auto* list = a_entry[a_key].as_array()) {
                for (const auto& node : *list) {
                    if (const auto text = node.value<std::string>()) {
                        if (auto* form = LookupAs<T>(*text)) {
                            a_add(form);
                        }
                    }
                }
            }
        }

        void FillHeadPart(RE::BGSHeadPart* a_part, const toml::table& a_entry)
        {
            a_part->SetModel(Text(a_entry, "model").c_str());
            a_part->type = static_cast<RE::BGSHeadPart::HeadPartType>(a_entry["part_type"].value_or(0));
            a_part->flags = static_cast<RE::BGSHeadPart::Flag>(a_entry["flags"].value_or(0));
            a_part->textureSet = Ref<RE::BGSTextureSet>(a_entry, "texture_set");
            a_part->color = Ref<RE::BGSColorForm>(a_entry, "color");
            a_part->validRaces = Ref<RE::BGSListForm>(a_entry, "valid_races");
            for (std::uint32_t i = 0; i < RE::BGSHeadPart::MorphIndices::kTotal; ++i) {
                const auto* tris = a_entry["tris"].as_array();
                const auto  tri = tris && i < tris->size() ? (*tris)[i].value_or(std::string{}) : std::string{};
                a_part->morphs[i].SetModel(tri.c_str());
            }
            EachRef<RE::BGSHeadPart>(a_entry, "extra_parts", [&](auto* p) { a_part->extraParts.push_back(p); });
        }

        bool Same(const RE::BSResource::ID& a, const RE::BSResource::ID& b)
        {
            return a.file == b.file && a.dir == b.dir && std::memcmp(a.ext, b.ext, sizeof(a.ext)) == 0;
        }

        // The game finds a texture set's textures by an ID made from each path.
        // Which form of the path it hashes is read off a texture set the game
        // loaded itself, so made ones get the same IDs.
        const std::string& TexturePrefix()
        {
            static const std::string prefix = [] {
                auto* handler = RE::TESDataHandler::GetSingleton();
                for (auto* set : handler->GetFormArray<RE::BGSTextureSet>()) {
                    const char* name = set ? set->textures[0].textureName.c_str() : nullptr;
                    if (!name || !*name || set->textureFileIDs[0].file == 0) {
                        continue;
                    }
                    for (const auto* candidate : { "textures\\", "", "data\\textures\\" }) {
                        RE::BSResource::ID id;
                        id.GenerateFromPath((std::string{ candidate } + name).c_str());
                        if (Same(id, set->textureFileIDs[0])) {
                            SKSE::log::info("Texture IDs use the prefix \"{}\".", candidate);
                            return std::string{ candidate };
                        }
                    }
                    break;
                }
                SKSE::log::warn("Couldn't tell how texture IDs are made; using \"textures\\\".");
                return std::string{ "textures\\" };
            }();
            return prefix;
        }

        void FillTextureSet(RE::BGSTextureSet* a_set, const toml::table& a_entry)
        {
            if (const auto* textures = a_entry["textures"].as_array()) {
                for (std::uint32_t i = 0; i < RE::BSTextureSet::Textures::kUsedTotal && i < textures->size(); ++i) {
                    const auto path = (*textures)[i].value_or(std::string{});
                    // Straight into the slot, as the plugin stores it (the game's own path setter
                    // numbers its slots differently and put the skin layer in the wrong one).
                    a_set->textures[i].textureName = path.c_str();
                    if (!path.empty()) {
                        a_set->textureFileIDs[i].GenerateFromPath((TexturePrefix() + path).c_str());
                    }
                }
            }
            a_set->flags = static_cast<RE::BGSTextureSet::Flag>(a_entry["flags"].value_or(0));
        }

        void FillColor(RE::BGSColorForm* a_color, const toml::table& a_entry)
        {
            if (const auto* rgb = a_entry["rgb"].as_array(); rgb && rgb->size() >= 3) {
                a_color->color.red = static_cast<std::uint8_t>((*rgb)[0].value_or(0));
                a_color->color.green = static_cast<std::uint8_t>((*rgb)[1].value_or(0));
                a_color->color.blue = static_cast<std::uint8_t>((*rgb)[2].value_or(0));
            }
            a_color->flags = static_cast<RE::BGSColorForm::Flag>(a_entry["flags"].value_or(0));
        }

        void FillList(RE::BGSListForm* a_list, const toml::table& a_entry)
        {
            EachRef<RE::TESForm>(a_entry, "forms", [&](auto* f) { a_list->AddForm(f); });
        }

        void FillArmor(RE::TESObjectARMO* a_armor, const toml::table& a_entry)
        {
            a_armor->race = Ref<RE::TESRace>(a_entry, "race");
            a_armor->bipedModelData.bipedObjectSlots = static_cast<RE::BIPED_MODEL::BipedObjectSlot>(a_entry["slots"].value_or(0u));
            a_armor->bipedModelData.armorType = static_cast<RE::BIPED_MODEL::ArmorType>(a_entry["armor_type"].value_or(2));
            EachRef<RE::TESObjectARMA>(a_entry, "addons", [&](auto* a) { a_armor->armorAddons.push_back(a); });
        }

        void FillAddon(RE::TESObjectARMA* a_addon, const toml::table& a_entry)
        {
            a_addon->race = Ref<RE::TESRace>(a_entry, "race");
            a_addon->bipedModelData.bipedObjectSlots = static_cast<RE::BIPED_MODEL::BipedObjectSlot>(a_entry["slots"].value_or(0u));
            for (std::size_t sex = 0; sex < 2; ++sex) {
                const auto* models = a_entry["models"].as_array();
                const auto* firsts = a_entry["models_1st"].as_array();
                const auto* skins = a_entry["skins"].as_array();
                const auto* priorities = a_entry["priorities"].as_array();
                if (models && sex < models->size()) {
                    a_addon->bipedModels[sex].SetModel((*models)[sex].value_or(std::string{}).c_str());
                }
                if (firsts && sex < firsts->size()) {
                    a_addon->bipedModel1stPersons[sex].SetModel((*firsts)[sex].value_or(std::string{}).c_str());
                }
                if (skins && sex < skins->size()) {
                    const auto text = (*skins)[sex].value_or(std::string{});
                    a_addon->skinTextures[sex] = text.empty() ? nullptr : LookupAs<RE::BGSTextureSet>(text);
                }
                if (priorities && sex < priorities->size()) {
                    a_addon->data.priorities[sex] = static_cast<std::int8_t>((*priorities)[sex].value_or(0));
                }
                // The weight slider flag (bit 2 of the plugin's byte): without it the
                // body isn't blended by weight, and the neck stops matching the face.
                const auto* sliders = a_entry["weight_slider"].as_array();
                if (sliders && sex < sliders->size()) {
                    a_addon->data.modelRange[sex] = (*sliders)[sex].value_or(false) ? 2 : 0;
                }
            }
            a_addon->data.detectionSoundValue = static_cast<std::int8_t>(a_entry["detection_sound"].value_or(0));
            a_addon->data.weaponAdjust = static_cast<float>(a_entry["weapon_adjust"].value_or(0.0));
            EachRef<RE::TESRace>(a_entry, "additional_races", [&](auto* r) { a_addon->additionalRaces.push_back(r); });
        }
    }

    RE::TESForm* Lookup(std::string_view a_text)
    {
        if (auto* loaded = LookupLoaded(a_text)) {
            return loaded;
        }
        const auto it = g_made.find(Lower(a_text));
        return it == g_made.end() ? nullptr : it->second;
    }

    void Load(const toml::table& a_list)
    {
        const auto* forms = a_list["forms"].as_array();
        if (!forms) {
            return;
        }

        // Make every missing form first, so links between them resolve.
        std::vector<std::pair<RE::TESForm*, const toml::table*>> made;
        for (const auto& node : *forms) {
            const auto* entry = node.as_table();
            if (!entry) {
                continue;
            }
            const auto key = Text(*entry, "key");
            if (key.empty() || LookupLoaded(key)) {
                continue;
            }
            auto* form = MakeOfType(Text(*entry, "type"));
            if (!form) {
                SKSE::log::warn("Couldn't make {} ({}).", key, Text(*entry, "type"));
                continue;
            }
            // Face meshes name each shape after its head part's editor ID.
            if (const auto editor_id = Text(*entry, "editor_id"); !editor_id.empty()) {
                form->SetFormEditorID(editor_id.c_str());
            }
            g_made[Lower(key)] = form;
            made.emplace_back(form, entry);
        }

        for (const auto& [form, entry] : made) {
            if (auto* part = form->As<RE::BGSHeadPart>()) {
                FillHeadPart(part, *entry);
            } else if (auto* set = form->As<RE::BGSTextureSet>()) {
                FillTextureSet(set, *entry);
            } else if (auto* color = form->As<RE::BGSColorForm>()) {
                FillColor(color, *entry);
            } else if (auto* list = form->As<RE::BGSListForm>()) {
                FillList(list, *entry);
            } else if (auto* armor = form->As<RE::TESObjectARMO>()) {
                FillArmor(armor, *entry);
            } else if (auto* addon = form->As<RE::TESObjectARMA>()) {
                FillAddon(addon, *entry);
            }
        }

        SKSE::log::info("Forms made for faces from disabled mods: {}.", made.size());

    }
}
