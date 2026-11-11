using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// The history behind a card (NativeCardHistory finds the facts and draws them when the player hovers the card). Pure: no Unity or
// engine types, so the launcher tests run it.
// Some cards draw on what happened earlier in the game: they resurrect minions that died, recast the spells their owner cast, or
// repeat the Battlecries they played. The card's text says which (HistoryQuery.For); the list is what the game has recorded so far,
// for the card's owner (or their opponent, for "enemy" minions).
internal enum HistoryKind { None, Died, Spells, Battlecries }

// One recorded card: what it was, and what the filters need.
internal sealed class RecordedCard
{
    internal string Id;
    internal string Name;
    internal int Cost;
    internal bool Legendary;
    // Its minion types, by the engine's names; empty for none.
    internal IReadOnlyCollection<string> Races = new string[0];
    internal bool Deathrattle;
}

internal sealed class HistoryQuery
{
    internal HistoryKind Kind;
    // Their minions, not the owner's: "enemy minions that died".
    internal bool Enemy;
    // Only Deathrattle minions: "your Deathrattle minions that died".
    internal bool Deathrattle;
    // Only this minion type (the engine's name for it, such as BEAST or MECHANICAL); null for any.
    internal string Race;

    private static readonly Dictionary<string, string> Races = new Dictionary<string, string> {
        { "beast", "BEAST" }, { "demon", "DEMON" }, { "dragon", "DRAGON" }, { "elemental", "ELEMENTAL" }, { "mech", "MECHANICAL" },
        { "murloc", "MURLOC" }, { "naga", "NAGA" }, { "pirate", "PIRATE" }, { "totem", "TOTEM" }, { "undead", "UNDEAD" },
        { "quilboar", "QUILBOAR" }, { "draenei", "DRAENEI" },
    };

    // What a card's rules text (markup removed) asks of the history; null when it asks nothing.
    internal static HistoryQuery For(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ");
        if (Regex.IsMatch(t, @"spells? you'?ve (cast|played) this game|spells? you (cast|played) this game"))
            return new HistoryQuery { Kind = HistoryKind.Spells };
        if (Regex.IsMatch(t, @"battlecr(y|ies) you'?ve played|battlecry minions? you'?ve played"))
            return new HistoryQuery { Kind = HistoryKind.Battlecries };
        // The words that say whose and which minions: before "that died", or after "Resurrect".
        var died = Regex.Match(t, @"([a-z' ]{0,40})\bthat died this (game|match)");
        var raise = Regex.Match(t, @"\bresurrect([a-z0-9' ]{0,40})");
        if (!died.Success && !raise.Success) return null;
        string before = died.Success ? died.Groups[1].Value : raise.Groups[1].Value;
        var query = new HistoryQuery {
            Kind = HistoryKind.Died,
            Enemy = Regex.IsMatch(before, @"\b(enemy|opponent'?s?)\b"),
            Deathrattle = Regex.IsMatch(before, @"\bdeathrattle\b"),
        };
        foreach (var race in Races)
            if (Regex.IsMatch(before, @"\b" + race.Key + @"s?\b")) { query.Race = race.Value; break; }
        return query;
    }

    internal bool Keeps(RecordedCard card) =>
        card != null && (!Deathrattle || card.Deathrattle) && (Race == null || card.Races.Contains(Race) || card.Races.Contains("ALL"));

    internal string Title(int count)
    {
        string what = Kind == HistoryKind.Spells ? "Spells cast this game"
            : Kind == HistoryKind.Battlecries ? "Battlecries played this game"
            : (Enemy ? "Enemy " : "Friendly ") + (Deathrattle ? "Deathrattle " : "") + (Race == null ? "minions" : RaceName(Race) + "s") + " that died";
        return what + " (" + count + ")";
    }

    private static string RaceName(string race)
    {
        foreach (var pair in Races) if (pair.Value == race) return char.ToUpperInvariant(pair.Key[0]) + pair.Key.Substring(1);
        return race;
    }

    // The kept cards, one entry per card in the order they first came, with how many times.
    internal List<KeyValuePair<RecordedCard, int>> Entries(IEnumerable<RecordedCard> cards)
    {
        var order = new List<RecordedCard>();
        var counts = new Dictionary<string, int>();
        foreach (var card in cards.Where(Keeps))
        {
            string key = card.Id ?? card.Name;
            if (!counts.ContainsKey(key)) { counts[key] = 0; order.Add(card); }
            counts[key]++;
        }
        return order.Select(card => new KeyValuePair<RecordedCard, int>(card, counts[card.Id ?? card.Name])).ToList();
    }
}
