using System;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model.Entities;

namespace SabberStoneCore.Actions
{
    public static class CombatRules
    {
        public const GameTag BonusRace = (GameTag)31360;
        public const GameTag BonusAmount = (GameTag)31361;
        public const GameTag DoubleRace = (GameTag)31362;
        public const GameTag OwnerTurnOnly = (GameTag)31364;
        public const GameTag IgnoreTauntAura = (GameTag)31365;
        public const GameTag Cleave = (GameTag)31366;
        public const GameTag ReplaceHeroDamage = (GameTag)31367;
        internal const GameTag DeathPasses = (GameTag)31368;
        internal const GameTag PreparationDepth = (GameTag)31369;
        public const GameTag FireImmune = (GameTag)4128;
        public static bool IsInternalTag(GameTag tag) => (int)tag >= 31360 && (int)tag <= 31369;

        private static bool Active(Minion minion) => !minion.IsSilenced && !minion.Untouchable;
        public static bool IgnoresTaunt(ICharacter source) => source.Controller.BoardZone
            .Any(m => Active(m) && m[IgnoreTauntAura] > 0);

        public static int AttackLimit(ICharacter source) => source[GameTag.MEGA_WINDFURY] > 0 ? 4 : source.HasWindfury ? 2 : 1;

        // The card data also puts tag 4128 on Captain Crowley, Tortolla, The Great Dracorex, Razidir and some spells and
        // locations; only Fyrakk is Immune to Fire spells. The tag still switches it off when Fyrakk is silenced.
        public static bool PreventsFire(CardType sourceType, int school, ICharacter target) =>
            sourceType == CardType.SPELL && school == 2 && target[FireImmune] > 0
            && target.Card.AssetId == CardSets.Client.Sets.FyrakkTheBlazing.DbfId
            && !(target is Minion minion && minion.IsSilenced);

        internal static int Outgoing(IPlayable source, int damage)
        {
            if (damage <= 0) return damage;
            long addition = source[GameTag.OUTGOING_DAMAGE_ADJUSTMENT];
            long multiplier = Math.Max(1, source[GameTag.OUTGOING_DAMAGE_MULTIPLIER]);
            foreach (var provider in source.Controller.BoardZone)
            {
                if (!Active(provider) || provider[OwnerTurnOnly] > 0 && source.Game.CurrentPlayer != source.Controller) continue;
                if (provider[BonusRace] > 0 && source.Card.IsRace((Race)provider[BonusRace])) addition += provider[BonusAmount];
                if (provider[DoubleRace] > 0 && source.Card.IsRace((Race)provider[DoubleRace])) multiplier = Math.Min(int.MaxValue, multiplier * 2);
            }
            return (int)Math.Min(int.MaxValue, Math.Min(int.MaxValue, Math.Max(0L, damage + addition)) * multiplier);
        }

        internal static bool ReplaceDamage(Hero target, int damage)
        {
            var weapon = target?.Weapon;
            if (damage <= 0 || weapon == null || weapon.Durability <= 0 || weapon[GameTag.SILENCED] != 0
                || weapon[ReplaceHeroDamage] == 0) return false;
            weapon.Damage++;
            return true;
        }

        internal static Minion[] CleaveTargets(ICharacter source, ICharacter target)
        {
            if (!(target is Minion defender) || source[Cleave] == 0 || source[GameTag.SILENCED] > 0)
                return Array.Empty<Minion>();
            return defender.GetAdjacentMinions();
        }
    }
}
