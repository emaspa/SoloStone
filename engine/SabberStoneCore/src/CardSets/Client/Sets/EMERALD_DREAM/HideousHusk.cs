using System;
using System.Linq;
using SabberStoneCore.EffectSystem;
using SabberStoneCore.Enchants;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.SimpleTasks;
using SabberStoneCore.Triggers;

namespace SabberStoneCore.CardSets.Client.Sets
{
    internal static class HideousHusk
    {
        internal const int DbfId = 114301;
        internal static void Configure(Card card)
        {
            card.Implemented = true;
            card.ExcludeFromLegacyRandomPools = true;
            card.Power = new Power { PowerTask = new CustomTask((g, c, s, t, stack) => {
                var ctx = new EffectContext(g, c.Id, s.Id, stack: stack);
                int position = s is Minion source && source.Zone == c.BoardZone ? source.ZonePosition + 1 : c.BoardZone.SlotCount;
                for (int i = 0; i < 2; i++) ctx.Summon("EDR_810t", position + i);
            }) };
        }
        private static int Amount(IPlayable source) => 1 + source.Controller.BoardZone.Count(m =>
            m.Card.Id == "EDR_810" && !m.IsSilenced && !m.IsDead && !m.Untouchable);

        internal static void ConfigureLeech(Card card)
        {
            card.Implemented = true;
            card.HistoryDisplay(GameTag.TAG_SCRIPT_DATA_NUM_1, Amount);
            card.Power = new Power { Trigger = new Trigger(TriggerType.TURN_END) {
                SingleTask = new CustomTask((g, c, s, t, stack) => {
                    var source = (Minion)s;
                    if (source.Zone != c.BoardZone || source.IsDead) return;
                    var candidates = c.Opponent.BoardZone.Where(m => !m.Untouchable).Cast<Character>()
                        .Append(c.Opponent.Hero).Where(m => !m.IsDead).ToArray();
                    if (candidates.Length == 0) return;
                    var ctx = new EffectContext(g, c.Id, s.Id, stack: stack);
                    int health = candidates.Min(m => m.Health);
                    var victim = ctx.SampleDistinct(candidates.Where(m => m.Health == health).ToArray(), 1)[0];
                    int amount = Math.Min(Amount(source), victim.BaseHealth);
                    // A Health enchantment, not damage: the victim keeps its damage, so its current Health falls with its
                    // maximum (a 1/3 minion with 2 damage that loses 1 dies), and the hero's maximum and current Health rise.
                    ctx.GiveBuff(victim.Id, "EDR_810e", amount);
                    ctx.GiveBuff(c.Hero.Id, "EDR_810e2", amount);
                })
            } };
        }
        internal static void ConfigureLoss(Card card)
        {
            card.Implemented = true;
            card.Power = new Power { Enchant = new Enchant(Health.Effect(EffectOperator.SUB, 0)) { UseScriptTag = true } };
        }
        internal static void ConfigureGain(Card card)
        {
            card.Implemented = true;
            card.Power = new Power { Enchant = new Enchant(Health.Effect(EffectOperator.ADD, 0)) { UseScriptTag = true } };
        }
    }
}
