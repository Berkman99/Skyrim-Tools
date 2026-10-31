#include "PCH.h"

#include "Picks/Picks.h"

#include "Files/Redirect.h"
#include "Picks/Forms.h"
#include "Picks/Looks.h"
#include "Picks/Voices.h"

namespace NAM::Picks
{
    namespace
    {
        // Read once when the game has loaded its plugins; the main thread only uses them afterwards.
        std::vector<Looks::Look>                               g_looks;
        std::unordered_map<RE::TESNPC*, const Looks::Look*> g_byNpc;

        void ApplyAll()
        {
            for (const auto& look : g_looks) {
                Looks::Apply(look);
            }
        }

        // The body an NPC wears: its skin, and for each piece whether it covers the NPC's race and has a model for its sex.
        void LogBody(RE::Actor* a_actor)
        {
            const auto* base = a_actor->GetActorBase();
            auto*       race = a_actor->GetRace();
            const auto* skin = a_actor->GetSkin();
            if (!base || !skin) {
                SKSE::log::info("{:08X}: no skin.", base ? base->GetFormID() : 0);
                return;
            }
            const auto sex = base->IsFemale() ? RE::SEX::kFemale : RE::SEX::kMale;
            std::string pieces;
            for (auto* arma : skin->armorAddons) {
                if (!arma) {
                    continue;
                }
                const auto* model = arma->bipedModels[sex].GetModel();
                pieces += std::format(" [{:08X} race {} model {}]", arma->GetFormID(), arma->IsValidRace(race) ? "ok" : "NO", model && *model ? "yes" : "NO");
            }
            SKSE::log::info("{:08X}: wears skin {:08X} (race {:08X}, base race {:08X}, opposite-gender animations {}):{}", base->GetFormID(),
                skin->GetFormID(), race ? race->GetFormID() : 0, base->race ? base->race->GetFormID() : 0,
                base->UsesOppositeGenderAnims() ? "on" : "off", pieces);
        }

        // An NPC the pick made the other sex gets its whole 3D rebuilt (body, skeleton, animations); the others a redraw.
        void Redraw(RE::Actor* a_actor)
        {
            if (!a_actor || !a_actor->Get3D()) {
                return;
            }
            const auto it = g_byNpc.find(a_actor->GetActorBase());
            if (it == g_byNpc.end() || !it->second->sexChanged) {
                a_actor->Update3DModel();
                return;
            }
            const auto start = std::chrono::steady_clock::now();
            a_actor->DoReset3D(true);
            const auto took = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
            const auto* base = a_actor->GetActorBase();
            SKSE::log::info("{:08X} changed sex: rebuilt in {:.2f} ms ({}).", base->GetFormID(), took, base->IsFemale() ? "female" : "male");
            LogBody(a_actor);
        }

        // NPCs already in view keep the 3D they were drawn with until they're redrawn.
        void RedrawLoaded()
        {
            auto* lists = RE::ProcessLists::GetSingleton();
            if (!lists) {
                return;
            }
            for (const auto& handle : lists->highActorHandles) {
                auto actor = handle.get();
                if (actor && g_byNpc.contains(actor->GetActorBase())) {
                    Redraw(actor.get());
                }
            }
        }

        // A picked NPC coming into view: its values once more, in case a save brought back old ones.
        class Appearing : public RE::BSTEventSink<RE::TESObjectLoadedEvent>
        {
        public:
            RE::BSEventNotifyControl ProcessEvent(const RE::TESObjectLoadedEvent* a_event, RE::BSTEventSource<RE::TESObjectLoadedEvent>*) override
            {
                if (!a_event || !a_event->loaded) {
                    return RE::BSEventNotifyControl::kContinue;
                }
                auto* actor = RE::TESForm::LookupByID<RE::Actor>(a_event->formID);
                auto* npc = actor ? actor->GetActorBase() : nullptr;
                if (const auto it = npc ? g_byNpc.find(npc) : g_byNpc.end(); it != g_byNpc.end()) {
                    Looks::Apply(*it->second);
                    const auto handle = actor->GetHandle();
                    SKSE::GetTaskInterface()->AddTask([handle] { Redraw(handle.get().get()); });
                }
                return RE::BSEventNotifyControl::kContinue;
            }
        };

        void Load()
        {
            const auto path = SavedFolder() / "Picks.toml";
            if (!std::filesystem::exists(path)) {
                SKSE::log::info("No picks list at {}.", path.string());
                return;
            }
            toml::table list;
            try {
                list = toml::parse_file(path.string());
            } catch (const toml::parse_error& e) {
                SKSE::log::error("{}: {}", path.string(), e.description());
                return;
            }

            Files::Redirect::Load(list);
            Forms::Load(list);
            g_looks = Looks::Load(list);  // drops the files of NPCs left as the game has them
            Files::Redirect::Start();
            Files::Redirect::SelfCheck();
            std::unordered_map<RE::TESNPC*, RE::BGSVoiceType*> speaksAs;
            for (const auto& look : g_looks) {
                g_byNpc[look.npc] = &look;
                if (look.speaksAs) {
                    speaksAs[look.npc] = look.speaksAs;
                }
            }
            ApplyAll();
            Voices::Set(std::move(speaksAs));

            static Appearing appearing;
            if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
                events->AddEventSink<RE::TESObjectLoadedEvent>(&appearing);
            }
        }

        void OnMessage(SKSE::MessagingInterface::Message* a_message)
        {
            switch (a_message->type) {
            case SKSE::MessagingInterface::kDataLoaded:
                Load();
                break;
            case SKSE::MessagingInterface::kPostLoadGame:
            case SKSE::MessagingInterface::kNewGame:
                SKSE::GetTaskInterface()->AddTask([] {
                    ApplyAll();
                    RedrawLoaded();
                });
                break;
            default:
                break;
            }
        }
    }

    void Start()
    {
        SKSE::GetMessagingInterface()->RegisterListener(OnMessage);
    }
}
