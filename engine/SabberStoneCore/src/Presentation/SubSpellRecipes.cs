using System.Collections.Generic;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;

namespace SabberStoneCore.Presentation
{
    internal enum VisualPhase { Damage, DamageGroup, Heal, BoardBuff, Draw, Create, Summon }
    internal enum VisualSource { Hero, Effect, Inherit }
    internal enum VisualTargets { Resolved, None }

    // Pinned 37.0.0.254477. Admission/provenance: data/fx/subspell-recipes.tsv.
    // shortcut: these substitutes have packet tests, coordinator must accept native completion before rollout.
    internal sealed class SubSpellRecipe
    {
        internal readonly string Prefab;
        internal readonly VisualSource Source;
        internal readonly VisualTargets Targets;
        internal readonly bool Pulse, Group;
        internal SubSpellRecipe(string prefab, VisualSource source, VisualTargets targets, bool pulse = false, bool group = false)
        { Prefab = prefab; Source = source; Targets = targets; Pulse = pulse; Group = group; }
    }

    internal static class SubSpellRecipes
    {
        private static readonly Dictionary<(string, BlockType, int, int, int, VisualPhase), SubSpellRecipe> Recipes = Build();
        private static Dictionary<(string, BlockType, int, int, int, VisualPhase), SubSpellRecipe> Build()
        {
            var result = new Dictionary<(string, BlockType, int, int, int, VisualPhase), SubSpellRecipe>();
            void Add(string cards, VisualPhase phase, string prefab, VisualTargets targets = VisualTargets.Resolved,
                bool pulse = false, BlockType block = BlockType.POWER, int keyword = 0, int subOption = -1, VisualSource source = VisualSource.Hero, bool group = false, int effectIndex = 0)
            {
                foreach (string card in cards.Split(' '))
                    result.Add((card, block, effectIndex, subOption, keyword, phase), new SubSpellRecipe(prefab, source, targets, pulse, group));
            }
            Add("TIME_001 JAIL_470 JAIL_225 MEND_302 TIME_614 JAIL_881 TIME_609", VisualPhase.Damage,
                "ReuseFX_Generic_Missile_Cannon_Super_Quickest:ed9fdef57cbdcbb44bbfd9f06c1c8d24");
            Add("END_000", VisualPhase.Damage, "ShadowBolt_FX:e6ce5b6e7e1250e4a92dac52a3508671");
            // FIR_909 repeated-cast live cleanup accepted 2026-11-02; same per-hit missile, including spell fan-out.
            // Grouped full-board waves keep their existing pulses; the remaining quarantined routes are listed in docs/design/damage-fx-coverage-20261027.md.
            Add("FIR_909 DINO_406 END_025 EDR_255 JAIL_801 CATA_585 CATA_582 CATA_303 ONY_018t2 BE_554 BE_153 BE_153t CAP_101 FIR_916 FIR_939 JAIL_307 TLC_227", VisualPhase.Damage, "ReuseFX_Fire_Missile_Fireball_Medium_Super_Simultaneous:161d5760bd9de1e41baf4c5611e61eed");
            // Erupting Volcano: native CardDef has no authored play spell (empty census), so the activation's resolved hits
            // had splats but no missile. Board location is the visual origin, as for Past Silvermoon; same missile as the spell routes.
            Add("CATA_584", VisualPhase.Damage, "ReuseFX_Fire_Missile_Fireball_Medium_Super_Simultaneous:161d5760bd9de1e41baf4c5611e61eed", source: VisualSource.Effect);
            // Location-granted Deathrattle damage, same census-empty gap: composite Elise locations (all enemies, hero-sourced like
            // the other Deathrattle damage routes: the dead location has no actor) and Past/Present/Future Gnomeregan's TIME_044t1e (2 to enemy hero).
            Add("TLC_100t1 TLC_100t2 TLC_100t3", VisualPhase.Damage, "ReuseFX_Physical_Missile_Generic_Super_Simultaneous:6ff0a03c3b353254d887e154c0f94e33", block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("TIME_044t1e", VisualPhase.Damage, "ReuseFX_Holy_Missile_Smite_Medium_Super_Quick_FastActorTriggers:30caf938696e3224bb610c73b35469e6", block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("CORE_ONY_018", VisualPhase.Damage, "ReuseFX_Fire_Missile_Fireball_Medium_Super_Simultaneous:161d5760bd9de1e41baf4c5611e61eed", subOption: 1);
            Add("CATA_485 CATA_533 BE_284 CATA_464t", VisualPhase.Damage, "ReuseFX_Frost_Missile_Snowball_Medium_Fast_Super_Simultaneous:4fecda861d7d78b4186c3a3c3091c1e5");
            Add("TIME_218 EDR_262 JAIL_805 EDR_263a", VisualPhase.Damage, "ReuseFX_Lightning_Missile_Bolt_Small_Super_Simultaneous:4e176ec954bdc6f4bb7494e521d35c8a");
            Add("CATA_475 CATA_999", VisualPhase.Damage, "ReuseFX_Lightning_Missile_Bolt_Small_Super_Simultaneous:4e176ec954bdc6f4bb7494e521d35c8a", block: BlockType.TRIGGER, source: VisualSource.Effect);
            Add("TIME_750 TIME_600", VisualPhase.Damage, "ReuseFX_Generic_Missile_Arrow_Super_Simultaneous:bbd7f9a5ebabd8e45b0a22cb4446f1d9");
            Add("EDR_851p EDR_941 CATA_978 TIME_441 CATA_489 EDR_476 MEND_500 BE_854 EDR_460t EDR_460 CATA_489t CATA_489t2 CATA_308", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de");
            Add("TIME_810 TIME_810t1 TIME_810t2", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de", source: VisualSource.Effect);
            Add("EDR_814 TLC_901 TLC_630t EDR_813b", VisualPhase.Damage, "ReuseFX_Decay_Missile_AcidGlob_Super_Simultaneous:1e8b03e89cf4f7a4d8dc823cf8132ad1");
            Add("BE_559 WW_001t CAP_006 TLC_620", VisualPhase.Damage, "ReuseFX_Physical_Missile_Generic_Super_Simultaneous:6ff0a03c3b353254d887e154c0f94e33");
            Add("JAIL_445", VisualPhase.Damage, "ReuseFX_Necrotic_Missile_Bone_Small_Super:d25c28f8f4b12964d8ab765bac643495");
            // Collapsing Star (Soul Immolation's hero power): its CardDef, and Soul Immolation's, author no play spell, so the
            // random hit had a splat but no missile. Shot from the hero, as for the other hero-power damage routes.
            Add("JAIL_EVENT_101hp", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0");
            Add("EDR_263", VisualPhase.Damage, "ReuseFX_Lightning_Missile_Bolt_Small_Super_Simultaneous:4e176ec954bdc6f4bb7494e521d35c8a", subOption: 0);
            Add("EDR_813", VisualPhase.Damage, "ReuseFX_Decay_Missile_AcidGlob_Super_Simultaneous:1e8b03e89cf4f7a4d8dc823cf8132ad1", subOption: 1);
            Add("EDR_570", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0", subOption: 0);
            Add("EDR_820", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0", subOption: 1);
            Add("EDR_570", VisualPhase.DamageGroup, "ReuseFX_Shadow_Fire_AE_Shockwave_Small_BothSides_Super:1f77cfbb2d94e3f4982cf07e849fa699", VisualTargets.None, subOption: 0, source: VisualSource.Effect, group: true);
            Add("EDR_820", VisualPhase.DamageGroup, "ReuseFX_Shadow_Fire_AE_Shockwave_Small_BothSides_Super:1f77cfbb2d94e3f4982cf07e849fa699", VisualTargets.None, subOption: 1, source: VisualSource.Effect, group: true);
            Add("CATA_304", VisualPhase.Damage, "ReuseFX_Physical_Impact_Slam_Super:4d1b99bee3bc05d489e03086bf27e295", source: VisualSource.Effect);
            Add("EDR_482e", VisualPhase.Damage, "ReuseFX_Physical_Impact_Slam_Super:4d1b99bee3bc05d489e03086bf27e295", block: BlockType.TRIGGER);
            Add("CATA_552 EDR_468 TLC_600 TLC_606", VisualPhase.Damage, "ReuseFX_Physical_Missile_Generic_Super_Simultaneous:6ff0a03c3b353254d887e154c0f94e33", source: VisualSource.Effect);
            Add("DINO_413", VisualPhase.Damage, "ReuseFX_Frost_Missile_Snowball_Medium_Fast_Super_Simultaneous:4fecda861d7d78b4186c3a3c3091c1e5", source: VisualSource.Effect);
            Add("EDR_459", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0", source: VisualSource.Effect);
            Add("TIME_004 TIME_858", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de", source: VisualSource.Effect);
            Add("CORE_RLK_505", VisualPhase.Damage, "ReuseFX_Necrotic_Missile_Bone_Small_Super:d25c28f8f4b12964d8ab765bac643495", source: VisualSource.Effect);
            Add("CS3_022 JAIL_379 JAIL_455 JAIL_501 TLC_633 TLC_825", VisualPhase.Damage, "ReuseFX_Physical_Missile_Generic_Super_Simultaneous:6ff0a03c3b353254d887e154c0f94e33", source: VisualSource.Effect);
            Add("DINO_138", VisualPhase.Damage, "ReuseFX_Decay_Missile_AcidGlob_Super_Simultaneous:1e8b03e89cf4f7a4d8dc823cf8132ad1", source: VisualSource.Effect);
            Add("JAIL_432", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0", source: VisualSource.Effect);
            Add("EDR_472 TIME_019 TLC_987", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de", source: VisualSource.Effect);
            Add("TIME_427 EDR_860", VisualPhase.Damage, "ReuseFX_Holy_Missile_Smite_Medium_Super_Quick_FastActorTriggers:30caf938696e3224bb610c73b35469e6", source: VisualSource.Effect);
            Add("CAP_803", VisualPhase.Damage, "ReuseFX_Holy_Missile_Smite_Medium_Super_Quick_FastActorTriggers:30caf938696e3224bb610c73b35469e6", block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("EDR_110", VisualPhase.Damage, "ReuseFX_Decay_Missile_AcidGlob_Super_Simultaneous:1e8b03e89cf4f7a4d8dc823cf8132ad1", block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("EDR_421", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0", block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("TLC_401", VisualPhase.Damage, "ReuseFX_Frost_Missile_Snowball_Medium_Fast_Super_Simultaneous:4fecda861d7d78b4186c3a3c3091c1e5", block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("JAIL_398", VisualPhase.Damage, "ReuseFX_Fire_AE_Flood_BothSides_Super:1d77444aaeef20d4494e2ad82c551341", VisualTargets.None, block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE, group: true);
            Add("EDR_842", VisualPhase.Damage, "ReuseFX_Decay_Missile_AcidGlob_Super_Simultaneous:1e8b03e89cf4f7a4d8dc823cf8132ad1", block: BlockType.TRIGGER);
            Add("TLC_478", VisualPhase.Damage, "ReuseFX_Physical_DemonHunter_AE_Whirlwind_BothSides_Small_Super_Quick:bc23f3d70f7ba4a4aa4ec2f8b4163e46", VisualTargets.None, block: BlockType.TRIGGER, group: true);
            Add("TLC_452t13 TLC_452t22 TLC_452t26", VisualPhase.Damage, "ReuseFX_Physical_Missile_Generic_Super_Simultaneous:6ff0a03c3b353254d887e154c0f94e33", source: VisualSource.Effect);
            Add("TLC_452t27", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de", source: VisualSource.Effect);
            Add("EDR_815", VisualPhase.Damage, "ReuseFX_Decay_Missile_AcidGlob_Super_Simultaneous:1e8b03e89cf4f7a4d8dc823cf8132ad1", block: BlockType.TRIGGER, source: VisualSource.Effect);
            Add("TLC_107 TLC_840", VisualPhase.Damage, "ReuseFX_Physical_Missile_Generic_Super_Simultaneous:6ff0a03c3b353254d887e154c0f94e33", block: BlockType.TRIGGER, source: VisualSource.Effect);
            Add("TIME_101", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de", block: BlockType.TRIGGER, source: VisualSource.Effect);
            Add("JAIL_881t", VisualPhase.Damage, "ReuseFX_Arcane_Missile_PurpleLightning_Super_Simultaneous:a40e6c32f4253314db432d7808a092de", block: BlockType.TRIGGER, subOption: 0);
            Add("JAIL_443t", VisualPhase.Damage, "ReuseFX_Physical_Impact_Slam_Super:4d1b99bee3bc05d489e03086bf27e295", block: BlockType.TRIGGER, subOption: 0);
            Add("TIME_025t JAIL_319", VisualPhase.Damage, "ReuseFX_Physical_Impact_Slam_Super:4d1b99bee3bc05d489e03086bf27e295");
            Add("JAIL_441 JAIL_891 JAIL_892 CATA_557 CAP_001 CATA_156 CATA_581 CORE_CATA_007 BE_086 BE_071 JAIL_941t CORE_SW_088 CATA_498 JAIL_515 CATA_785 EDR_820b EDR_570A", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0");
            Add("DINO_132", VisualPhase.Damage, "ReuseFX_Shadow_Missile_Blast_Small_Super_Simultaneous:e39b07044149c034d9b1d70182afe5b0", block: BlockType.TRIGGER, source: VisualSource.Effect);
            Add("CAP_107t", VisualPhase.Damage, "ReuseFX_Generic_Missile_Cannon_Super_Quickest:ed9fdef57cbdcbb44bbfd9f06c1c8d24",
                block: BlockType.TRIGGER, source: VisualSource.Effect);
            Add("CAP_107t", VisualPhase.Damage, "ReuseFX_Generic_Missile_Cannon_Super_Quickest:ed9fdef57cbdcbb44bbfd9f06c1c8d24",
                block: BlockType.TRIGGER, source: VisualSource.Effect, effectIndex: -1);
            Add("CATA_488", VisualPhase.Damage, "ReuseFX_Fire_AE_Flood_BothSides_Super:1d77444aaeef20d4494e2ad82c551341",
                VisualTargets.None, block: BlockType.TRIGGER, source: VisualSource.Effect, group: true);
            Add("TLC_601 CATA_526", VisualPhase.Damage, "ReuseFX_Physical_DemonHunter_AE_Whirlwind_BothSides_Small_Super_Quick:bc23f3d70f7ba4a4aa4ec2f8b4163e46",
                VisualTargets.None, source: VisualSource.Effect, group: true);
            Add("CATA_491", VisualPhase.Damage, "ReuseFX_Shadow_Fire_AE_Shockwave_Small_BothSides_Super:1f77cfbb2d94e3f4982cf07e849fa699",
                VisualTargets.None, source: VisualSource.Effect, group: true);
            Add("CATA_582", VisualPhase.DamageGroup, "ReuseFX_Fire_AE_Flood_BothSides_Super:1d77444aaeef20d4494e2ad82c551341",
                VisualTargets.None, source: VisualSource.Effect, group: true);
            Add("CATA_581", VisualPhase.DamageGroup, "ReuseFX_Shadow_Fire_AE_Shockwave_Small_BothSides_Super:1f77cfbb2d94e3f4982cf07e849fa699",
                VisualTargets.None, source: VisualSource.Effect, group: true);
            Add("EDR_820b EDR_570A", VisualPhase.DamageGroup, "ReuseFX_Shadow_Fire_AE_Shockwave_Small_BothSides_Super:1f77cfbb2d94e3f4982cf07e849fa699",
                VisualTargets.None, source: VisualSource.Effect, group: true);
            Add("JAIL_307", VisualPhase.DamageGroup, "ReuseFX_Fire_AE_Flood_BothSides_Super:1d77444aaeef20d4494e2ad82c551341",
                VisualTargets.None, source: VisualSource.Effect, group: true);
            Add("TIME_015 TIME_018 TIME_431", VisualPhase.Heal, "ReuseFX_Holy_BuffImpact_Heal_Small_Super:98a6ddb5b34da2a419a16dcab9a2ef2c");
            Add("DINO_417", VisualPhase.BoardBuff, "ReuseFX_Rage_BuffImpact_Small_Super:70ca51dd0af761f4d8793bfb07004f38");
            Add("JAIL_201", VisualPhase.BoardBuff, "ReuseFX_Beast_Impact_FangBite_Gold_Super:f25e8e68c2da89040b1477b8c4d989c6", VisualTargets.None, subOption: 0);
            Add("END_010", VisualPhase.BoardBuff, "ReuseFX_Holy_AE_HealingWave_Centered_BothSides_Super:c468a26646b575b45ae00505fea3c00f",
                subOption: 0, source: VisualSource.Effect, group: true);
            Add("END_010", VisualPhase.BoardBuff, "ReuseFX_Holy_AE_HealingWave_Centered_BothSides_Super:c468a26646b575b45ae00505fea3c00f",
                subOption: 1, source: VisualSource.Effect, group: true);
            Add("TIME_601", VisualPhase.Draw, "ReuseFX_DrawBoosh_DemonHunter_Small_Super:425f23a1c2069a9419bc4bff0c753427",
                VisualTargets.None, pulse: true);
            Add("DINO_434 CATA_556", VisualPhase.Create, "ReuseFX_SpawnToHand_Arcane_Runes_Super:da3a6dbbfa826a14da891041cfa32a3b", VisualTargets.None);
            Add("DINO_434", VisualPhase.Create, "ReuseFX_SpawnToHand_Arcane_Runes_Super:da3a6dbbfa826a14da891041cfa32a3b",
                VisualTargets.None, block: BlockType.TRIGGER, keyword: (int)GameTag.DEATHRATTLE);
            Add("TIME_018", VisualPhase.Create, "ReuseFX_Holy_SpawnToHand_Hero_Super:d469ef317c9243b49bbccdc5eeb0ac81", VisualTargets.None);
            Add("JAIL_454", VisualPhase.Summon,
                "ReuseFX_Necrotic_DeathKnight_OverrideSpawn_FromGround_Super:18936aca8a333214ba05320a8816a173", VisualTargets.None);
            return result;
        }

        internal static SubSpellRecipe Find(PowerHistoryBlockStart block, string sourceCard, VisualPhase phase)
        {
            if (block == null) return null;
            string identity = string.IsNullOrEmpty(block.EffectCardId) ? sourceCard : block.EffectCardId;
            return Recipes.TryGetValue((identity, block.BlockType, block.EffectIndex.GetValueOrDefault(),
                block.Index, block.TriggerKeyword.GetValueOrDefault(), phase), out var recipe) ? recipe : null;
        }
    }
}
