using System;
using System.Collections.Generic;
using System.Linq;

// Per-deck lifetime record for the deck picker (owner 2026-11-05): wins and losses of one deck across Ranked and Casual, folded from the
// settled ledger (LocalProfileData.Matches), never stored. So there is no schema change and the count is exactly the settlements:
// a settlement is one ledger row (Settle is idempotent per match id and the abandon-on-restart commit clears Pending in the same step),
// which means no double count on restart replay, concede or Rematch (a rematch is its own match row).
// Counting, from the player's side, as for ladder stars and the NPC head-to-head (LocalNpcRecord): Won = W; Lost (including the
// player's concession) and Abandoned (a match the client died in) = L; Tied is kept apart and shown nowhere; unsettled rows and rows
// without a result are skipped. Debug scenario matches never reach the ledger. The key is the match's Deck ("custom:<id>" for a built
// deck, the loaner id for a starter), so the record survives rename and edit (same id) and a deleted deck's record goes with it:
// custom ids are never reused, so nothing can ever ask for that key again.
public sealed class LocalDeckRecord
{
    public static readonly LocalDeckRecord None = new LocalDeckRecord(0, 0, 0, 0, 0);
    public int RankedWins { get; }
    public int RankedLosses { get; }
    public int CasualWins { get; }
    public int CasualLosses { get; }
    public int Ties { get; }
    public int Wins => RankedWins + CasualWins;
    public int Losses => RankedLosses + CasualLosses;
    public int Games => Wins + Losses + Ties;
    public LocalDeckRecord(int rankedWins, int rankedLosses, int casualWins, int casualLosses, int ties)
    { RankedWins = rankedWins; RankedLosses = rankedLosses; CasualWins = casualWins; CasualLosses = casualLosses; Ties = ties; }

    // The "custom" deck key of the pre-multi-deck save belongs to the legacy custom deck id.
    private static readonly string LegacyKey = "custom:" + LocalProfile.LegacyCustomDeckId;

    // The ledger key of a deck: what a match on it settles under (Begin's deck argument). ONE function for the start path
    // (NativeUIProbe.StartPicked) and the picker line (NativeUIProbe.DeckKeyOf), so the line can never look up a key the start never wrote.
    public static string KeyFor(bool customDeck, long nativeDeckId, string loanerId) =>
        customDeck ? "custom:" + nativeDeckId.ToString(System.Globalization.CultureInfo.InvariantCulture) : loanerId;

    public static bool SameDeck(string matchDeck, string deckKey) =>
        matchDeck != null && deckKey != null && (matchDeck == deckKey || (matchDeck == "custom" && deckKey == LegacyKey));

    public static LocalDeckRecord Of(IEnumerable<LocalMatchRecord> settled, string deckKey)
    {
        if (string.IsNullOrEmpty(deckKey) || settled == null) return None;
        int rankedWins = 0, rankedLosses = 0, casualWins = 0, casualLosses = 0, ties = 0;
        foreach (var match in settled)
        {
            if (match == null || !SameDeck(match.Deck, deckKey)) continue;
            bool casual = CasualRules.IsCasual(match.Mode);
            switch (match.Result)
            {
                case "Won": if (casual) casualWins++; else rankedWins++; break;
                case "Lost": case "Abandoned": if (casual) casualLosses++; else rankedLosses++; break;
                case "Tied": ties++; break;
            }
        }
        return new LocalDeckRecord(rankedWins, rankedLosses, casualWins, casualLosses, ties);
    }
}

public sealed partial class LocalProfile
{
    // The selected deck's record; empty for an unknown key. Reads the in-memory ledger only (no I/O, never throws on a bad key).
    public LocalDeckRecord DeckRecord(string deckKey) => LocalDeckRecord.Of(data.Matches, deckKey);

    // The selected deck's stats (DeckStats) over its settled matches, from the in-memory ledger.
    internal DeckStats DeckStatsFor(string deckKey) => string.IsNullOrEmpty(deckKey) ? DeckStats.Empty
        : DeckStats.Of(data.Matches.Where(match => match != null && LocalDeckRecord.SameDeck(match.Deck, deckKey)).Select(StatsGame));

    private static DeckStats.Game StatsGame(LocalMatchRecord match)
    {
        LocalDeck opponent = null;
        try { opponent = LocalDecks.Get(match.NpcDeckId ?? match.Opponent); } catch (Exception) { }
        string opponentClass = null;
        try { if (opponent?.Hero != null) opponentClass = LocalDecks.HeroClass(opponent.Hero).ToString(); } catch (Exception) { }
        return new DeckStats.Game { Result = match.Result, Cards = match.CardStats, OpponentClass = opponentClass,
            Casual = CasualRules.IsCasual(match.Mode), Opponent = opponent?.Name, Settled = match.SettledUtc, Turns = match.Turns };
    }

    // The stats screen's decks: every deck the player still has that has a decided game, most played first. A deleted deck's
    // matches stay in the ledger but have no deck to show them under.
    internal IReadOnlyList<DeckStatsEntry> DeckStatsBook()
    {
        var entries = new List<DeckStatsEntry>();
        var keys = data.Matches.Where(match => match?.Deck != null && match.SettledUtc != null).Select(match => match.Deck)
            .Distinct(StringComparer.Ordinal).ToList();
        var customs = CustomDecks;
        foreach (string matchKey in keys)
        {
            string key = matchKey == "custom" ? "custom:" + LegacyCustomDeckId.ToString(System.Globalization.CultureInfo.InvariantCulture) : matchKey;
            if (entries.Any(entry => entry.Key == key)) continue;
            string name = null, hero = null; IReadOnlyList<string> cards = null;
            if (key.StartsWith("custom:", StringComparison.Ordinal))
            {
                var deck = customs.FirstOrDefault(custom => "custom:" + custom.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == key);
                if (deck == null) continue;
                name = deck.Name; hero = deck.Hero; cards = deck.Cards.ToList();
            }
            else
            {
                LocalDeck deck = null;
                try { deck = LocalDecks.Get(key); } catch (Exception) { }
                if (deck == null) continue;
                name = deck.Name; hero = deck.Hero; cards = deck.CardIds;
            }
            var stats = DeckStatsFor(key);
            if (stats.Games == 0) continue;
            string heroClass = null;
            try { heroClass = hero == null ? null : LocalDecks.HeroClass(hero).ToString(); } catch (Exception) { }
            entries.Add(new DeckStatsEntry { Key = key, Name = string.IsNullOrWhiteSpace(name) ? "Deck" : name.Trim(), Class = heroClass, Cards = cards, Stats = stats });
        }
        return entries.OrderByDescending(entry => entry.Stats.Games).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToList();
    }
}

// The picker's text (pure, no Unity): "12 W - 7 L". An unplayed deck reads "0 W - 0 L" (shown, not hidden: the label does not pop in
// and out as the player pages through decks, and 0-0 is true). No percentage, no streak.
internal static class DeckRecordText
{
    internal static string Format(LocalDeckRecord record) =>
        (record ?? LocalDeckRecord.None).Wins.ToString(System.Globalization.CultureInfo.InvariantCulture) + " W - "
        + (record ?? LocalDeckRecord.None).Losses.ToString(System.Globalization.CultureInfo.InvariantCulture) + " L";
}
