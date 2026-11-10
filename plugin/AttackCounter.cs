using System;
using SabberStoneCore.Actions;
using SabberStoneCore.Model.Entities;

// The attack counters' sums, from the public board. The side whose turn it is: what it can still deal this turn (each
// character that can attack, times the attacks it has left). The other side: what it could deal on its next turn (minions
// that are not dormant, frozen or unable to attack, and the weapon: the hero's own attack this turn runs out).
// Windfury counts twice, Mega-Windfury four times. Taunt, Rush and the targets' health are not taken into account.
internal static class AttackCounter
{
    internal static int ThisTurn(Controller side)
    {
        int total = 0;
        foreach (Minion minion in side.BoardZone)
            if (minion.CanAttack) total += minion.AttackDamage * Math.Max(1, CombatRules.AttackLimit(minion) - minion.NumAttacksThisTurn);
        Hero hero = side.Hero;
        if (hero != null && hero.CanAttack) total += hero.AttackDamage * Math.Max(1, CombatRules.AttackLimit(hero) - hero.NumAttacksThisTurn);
        return total;
    }

    internal static int NextTurn(Controller side)
    {
        int total = 0;
        foreach (Minion minion in side.BoardZone)
            if (!minion.IsDormant && !minion.CantAttack && !minion.IsFrozen && minion.AttackDamage > 0)
                total += minion.AttackDamage * CombatRules.AttackLimit(minion);
        Hero hero = side.Hero;
        if (hero?.Weapon != null && !hero.IsFrozen && !hero.CantAttack && hero.Weapon.AttackDamage > 0)
            total += hero.Weapon.AttackDamage * CombatRules.AttackLimit(hero);
        return total;
    }

    internal static int For(Controller side, bool sidesTurn) => sidesTurn ? ThisTurn(side) : NextTurn(side);
}
