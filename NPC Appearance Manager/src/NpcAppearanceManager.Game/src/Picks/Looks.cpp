#include "PCH.h"

#include "Picks/Looks.h"

#include "Files/Redirect.h"
#include "Picks/Forms.h"

#include <Windows.h>

namespace NAM::Looks
{
    namespace
    {
        using HeadType = RE::BGSHeadPart::HeadPartType;
        using Template = RE::ACTOR_BASE_DATA::TEMPLATE_USE_FLAG;

        // The list is UTF-8; the game's names are Windows-1252, as plugins store them. A name that doesn't fit
        // Western text (typed in Russian, say) stays UTF-8, the way translated mods store theirs.
        std::string GameText(const std::string& a_utf8)
        {
            const auto wideLength = MultiByteToWideChar(CP_UTF8, 0, a_utf8.data(), static_cast<int>(a_utf8.size()), nullptr, 0);
            std::wstring wide(static_cast<std::size_t>(wideLength), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, a_utf8.data(), static_cast<int>(a_utf8.size()), wide.data(), wideLength);
            BOOL       lost = FALSE;
            const auto length = WideCharToMultiByte(1252, 0, wide.data(), wideLength, nullptr, 0, nullptr, &lost);
            if (lost) {
                return a_utf8;
            }
            std::string text(static_cast<std::size_t>(length), '\0');
            WideCharToMultiByte(1252, 0, wide.data(), wideLength, text.data(), length, nullptr, nullptr);
            return text;
        }

        template <class T>
        T* Form(const toml::table& a_entry, std::string_view a_key, std::vector<std::string>& a_lacking)
        {
            const auto text = a_entry[a_key].value<std::string>();
            if (!text || text->empty()) {
                return nullptr;
            }
            auto* form = Forms::LookupAs<T>(*text);
            if (!form) {
                a_lacking.push_back(std::format("its {} {}", a_key, *text));
            }
            return form;
        }

        // The game adds the race's head and mouth to an NPC whose record has none of its own (most vanilla NPCs),
        // so a list taken from such a record gets them too: they move the mouth and face when it talks.
        std::vector<RE::BGSHeadPart*> WithRaceParts(std::vector<RE::BGSHeadPart*> a_parts, RE::TESRace* a_race, bool a_female)
        {
            const auto has = [&](HeadType a_type) {
                return std::ranges::any_of(a_parts, [&](auto* p) {
                    return p && p->type == a_type && !p->flags.all(RE::BGSHeadPart::Flag::kIsExtraPart);
                });
            };
            const auto* data = a_race ? a_race->faceRelatedData[a_female ? RE::SEX::kFemale : RE::SEX::kMale] : nullptr;
            if (data && data->headParts) {
                const bool face = has(HeadType::kFace);
                const bool misc = has(HeadType::kMisc);
                for (auto* part : *data->headParts) {
                    if (part && ((part->type == HeadType::kFace && !face) || (part->type == HeadType::kMisc && !misc))) {
                        a_parts.push_back(part);
                    }
                }
            }
            return a_parts;
        }

        void SetHeadParts(RE::TESNPC* a_npc, const std::vector<RE::BGSHeadPart*>& a_parts)
        {
            const auto count = static_cast<std::int8_t>(std::min<std::size_t>(a_parts.size(), 127));
            if (a_npc->numHeadParts == count && a_npc->headParts && std::equal(a_parts.begin(), a_parts.begin() + count, a_npc->headParts)) {
                return;
            }
            auto* parts = count > 0 ? RE::calloc<RE::BGSHeadPart*>(static_cast<std::size_t>(count)) : nullptr;
            std::copy_n(a_parts.begin(), count, parts);
            a_npc->headParts = parts;
            a_npc->numHeadParts = count;
        }

        // An NPC that takes its looks from a template ignores its own: it gets the template's looks as its own
        // first, so the picked values below are the ones the game uses.
        void StopUsingTemplateLooks(RE::TESNPC* a_npc)
        {
            if (!a_npc->actorData.templateUseFlags.all(Template::kTraits)) {
                return;
            }
            if (auto* from = a_npc->baseTemplateForm ? a_npc->baseTemplateForm->As<RE::TESNPC>() : nullptr) {
                a_npc->race = from->race;
                a_npc->skin = from->skin;
                a_npc->height = from->height;
                a_npc->weight = from->weight;
                a_npc->voiceType = from->voiceType;
            }
            a_npc->actorData.templateUseFlags.reset(Template::kTraits);
        }
    }

    std::vector<Look> Load(const toml::table& a_list)
    {
        std::vector<Look> looks;
        const auto*       entries = a_list["looks"].as_array();
        if (!entries) {
            return looks;
        }

        for (const auto& node : *entries) {
            const auto* entry = node.as_table();
            const auto  target = entry ? (*entry)["target"].value<std::string>() : std::nullopt;
            auto*       npc = target ? Forms::LookupAs<RE::TESNPC>(*target) : nullptr;
            if (!npc) {
                if (target) {
                    SKSE::log::warn("{} isn't in the game.", *target);
                }
                continue;
            }

            // What it needs that isn't in the game (a mod removed, renamed or turned off since Save): then the NPC is
            // left as the game has it, never given part of its picked face.
            std::vector<std::string> lacking;
            if (const auto* faces = (*entry)["faces"].as_array()) {
                for (const auto& face : *faces) {
                    if (const auto path = face.value<std::string>(); path && Files::Redirect::IsMissing(*path)) {
                        lacking.push_back(std::format("its face file {}", *path));
                    }
                }
            }

            Look look;
            look.npc = npc;
            look.raceSkin = (*entry)["skin"].value<std::string>() == "none";
            look.skin = look.raceSkin ? nullptr : Form<RE::TESObjectARMO>(*entry, "skin", lacking);
            look.hairColor = Form<RE::BGSColorForm>(*entry, "hair_color", lacking);
            look.headTexture = Form<RE::BGSTextureSet>(*entry, "head_texture", lacking);
            look.race = Form<RE::TESRace>(*entry, "race", lacking);
            look.voice = Form<RE::BGSVoiceType>(*entry, "voice", lacking);
            look.speaksAs = Form<RE::BGSVoiceType>(*entry, "speaks_as", lacking);
            if (const auto value = (*entry)["height"].value<double>()) look.height = static_cast<float>(*value);
            if (const auto value = (*entry)["weight"].value<double>()) look.weight = static_cast<float>(*value);
            look.female = (*entry)["female"].value<bool>();
            if (look.female && npc->IsFemale() != *look.female) {
                look.sexChanged = true;
                SKSE::log::info("{}: sex {} -> {}, race {:08X} -> {:08X}, lines of voice {:08X} played in {:08X}, opposite-gender animations {} -> off.", *target,
                    npc->IsFemale() ? "female" : "male", *look.female ? "female" : "male",
                    npc->race ? npc->race->GetFormID() : 0, look.race ? look.race->GetFormID() : (npc->race ? npc->race->GetFormID() : 0),
                    npc->voiceType ? npc->voiceType->GetFormID() : 0, look.speaksAs ? look.speaksAs->GetFormID() : 0,
                    npc->UsesOppositeGenderAnims() ? "on" : "off");
            }
            if (const auto* tint = (*entry)["body_tint"].as_array(); tint && tint->size() == 3) {
                look.bodyTint = RE::Color{ static_cast<std::uint8_t>((*tint)[0].value_or(0)), static_cast<std::uint8_t>((*tint)[1].value_or(0)),
                    static_cast<std::uint8_t>((*tint)[2].value_or(0)), 0 };
            }
            if (const auto name = (*entry)["name"].value<std::string>()) {
                look.name = GameText(*name);
            }
            if (const auto* parts = (*entry)["head_parts"].as_array()) {
                look.setHeadParts = true;
                for (const auto& part : *parts) {
                    const auto text = part.value<std::string>();
                    if (auto* headPart = text ? Forms::LookupAs<RE::BGSHeadPart>(*text) : nullptr) {
                        look.headParts.push_back(headPart);
                    } else if (text) {
                        lacking.push_back(std::format("its head part {}", *text));
                    }
                }
            }
            if (!lacking.empty()) {
                if (const auto* faces = (*entry)["faces"].as_array()) {
                    for (const auto& face : *faces) {
                        if (const auto path = face.value<std::string>()) {
                            Files::Redirect::Drop(*path);
                        }
                    }
                }
                SKSE::log::warn("{} keeps its look from the game: {} isn't there anymore.", *target, lacking.front());
                for (const auto& what : lacking | std::views::drop(1)) {
                    SKSE::log::warn("{}: {} isn't there either.", *target, what);
                }
                continue;
            }
            looks.push_back(std::move(look));
        }

        SKSE::log::info("Picked NPCs: {}.", looks.size());
        return looks;
    }

    void Apply(const Look& a_look)
    {
        auto* npc = a_look.npc;
        StopUsingTemplateLooks(npc);

        if (a_look.race) npc->race = a_look.race;
        if (a_look.skin) npc->skin = a_look.skin;
        if (a_look.raceSkin) npc->skin = nullptr;
        if (a_look.height) npc->height = *a_look.height;
        if (a_look.weight) npc->weight = *a_look.weight;
        if (a_look.bodyTint) npc->bodyTintColor = *a_look.bodyTint;
        if (a_look.voice) npc->voiceType = a_look.voice;
        if (a_look.name) npc->fullName = *a_look.name;
        if (a_look.female) {
            if (*a_look.female) {
                npc->actorData.actorBaseFlags.set(RE::ACTOR_BASE_DATA::Flag::kFemale);
            } else {
                npc->actorData.actorBaseFlags.reset(RE::ACTOR_BASE_DATA::Flag::kFemale);
            }
        }
        // "Opposite gender animations" would give the new sex the old sex's animations: the NPC uses the new sex's own.
        if (a_look.sexChanged) {
            npc->actorData.actorBaseFlags.reset(RE::ACTOR_BASE_DATA::Flag::kOppositeGenderAnims);
        }
        if (a_look.hairColor) npc->SetHairColor(a_look.hairColor);
        if (a_look.headTexture) npc->SetFaceTexture(a_look.headTexture);
        if (a_look.setHeadParts) {
            SetHeadParts(npc, WithRaceParts(a_look.headParts, npc->race, npc->IsFemale()));
        }
    }
}
