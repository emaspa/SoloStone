using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

// What one settled match saw of the player's own cards, for the deck stats (DeckStats). Recorded by NativeMatch at the end of the
// match from the engine's history facts, so a Rewind is already undone. Only the player's starting 30 count: a card created in the
// match is in none of these lists. Every list keeps duplicates (two copies drawn is two entries). Additive and optional on
// LocalMatchRecord, written only when present: older saves stay byte-identical and their matches simply have no card stats.
public sealed class LocalMatchCardStats
{
    internal const int MaxCards = 64;
    // The opponent hero's class at the end of the match, an engine CardClass name ("MAGE").
    public string OpponentClass;
    // The mulligan: the cards offered, then the ones the player kept.
    [XmlArrayItem("Card")] public List<string> Offered = new List<string>();
    [XmlArrayItem("Card")] public List<string> Kept = new List<string>();
    // The hand after the mulligan: kept cards and their replacements (no Coin, no turn-one draw).
    [XmlArrayItem("Card")] public List<string> Opening = new List<string>();
    // Every starting card that reached the player's hand, the opening hand included.
    [XmlArrayItem("Card")] public List<string> Drawn = new List<string>();
    // Every starting card the player played from hand.
    [XmlArrayItem("Card")] public List<string> Played = new List<string>();

    internal static LocalMatchCardStats Clone(LocalMatchCardStats stats) => stats == null ? null : new LocalMatchCardStats {
        OpponentClass = stats.OpponentClass, Offered = stats.Offered?.ToList() ?? new List<string>(), Kept = stats.Kept?.ToList() ?? new List<string>(),
        Opening = stats.Opening?.ToList() ?? new List<string>(), Drawn = stats.Drawn?.ToList() ?? new List<string>(),
        Played = stats.Played?.ToList() ?? new List<string>()
    };

    private static readonly Regex CardId = new Regex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ClassName = new Regex("^[A-Z_]{1,32}$", RegexOptions.CultureInvariant);

    internal static bool Valid(LocalMatchCardStats stats)
    {
        if (stats == null) return false;
        if (stats.OpponentClass != null && !ClassName.IsMatch(stats.OpponentClass)) return false;
        foreach (var list in new[] { stats.Offered, stats.Kept, stats.Opening, stats.Drawn, stats.Played })
            if (list == null || list.Count > MaxCards || list.Any(id => id == null || !CardId.IsMatch(id))) return false;
        return stats.Kept.Count <= stats.Offered.Count && stats.Opening.Count <= stats.Drawn.Count;
    }
}

// One card's numbers over a deck's tracked games, HSReplay style: each game counts once per card, however many copies.
internal sealed class DeckCardStat
{
    internal int OfferedGames, KeptGames;      // games it was offered in the mulligan, and kept (at least one copy)
    internal int OpeningGames, OpeningWins;    // in the hand after the mulligan
    internal int DrawnGames, DrawnWins;        // reached the hand at any point, opening hand included
    internal int PlayedGames, PlayedWins;      // played from hand
}

// A deck's stats, pure: from the settled matches of one deck. Wins and losses count as for the deck picker's record
// (LocalDeckRecord): Won is a win; Lost (concessions included) and Abandoned are losses; Tied counts in no rate.
// Matches settled before card stats existed count in the record only.
internal sealed class DeckStats
{
    internal sealed class Game
    {
        internal string Result;
        internal LocalMatchCardStats Cards;
    }

    internal static readonly DeckStats Empty = new DeckStats();
    internal int Wins, Losses;
    // Games with card stats, and their wins: the base every card rate is measured against.
    internal int TrackedGames, TrackedWins;
    internal readonly Dictionary<string, int[]> ByClass = new Dictionary<string, int[]>(StringComparer.Ordinal); // class -> {wins, losses}
    internal readonly Dictionary<string, DeckCardStat> Cards = new Dictionary<string, DeckCardStat>(StringComparer.Ordinal);

    internal int Games => Wins + Losses;

    internal static DeckStats Of(IEnumerable<Game> games)
    {
        var stats = new DeckStats();
        foreach (var game in games ?? Enumerable.Empty<Game>())
        {
            if (game == null) continue;
            bool won = game.Result == "Won", lost = game.Result == "Lost" || game.Result == "Abandoned";
            if (!won && !lost) continue;
            if (won) stats.Wins++; else stats.Losses++;
            var cards = game.Cards;
            if (cards == null) continue;
            stats.TrackedGames++;
            if (won) stats.TrackedWins++;
            if (!string.IsNullOrEmpty(cards.OpponentClass))
            {
                if (!stats.ByClass.TryGetValue(cards.OpponentClass, out int[] record)) stats.ByClass[cards.OpponentClass] = record = new int[2];
                record[won ? 0 : 1]++;
            }
            foreach (string id in Distinct(cards.Offered)) stats.Card(id).OfferedGames++;
            foreach (string id in Distinct(cards.Kept)) stats.Card(id).KeptGames++;
            foreach (string id in Distinct(cards.Opening)) { var card = stats.Card(id); card.OpeningGames++; if (won) card.OpeningWins++; }
            foreach (string id in Distinct(cards.Drawn)) { var card = stats.Card(id); card.DrawnGames++; if (won) card.DrawnWins++; }
            foreach (string id in Distinct(cards.Played)) { var card = stats.Card(id); card.PlayedGames++; if (won) card.PlayedWins++; }
        }
        return stats;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> ids) => (ids ?? Enumerable.Empty<string>()).Where(id => id != null).Distinct(StringComparer.Ordinal);

    private DeckCardStat Card(string id)
    {
        if (!Cards.TryGetValue(id, out var card)) Cards[id] = card = new DeckCardStat();
        return card;
    }

    internal DeckCardStat For(string id) => id != null && Cards.TryGetValue(id, out var card) ? card : null;

    internal int[] Against(string opponentClass) =>
        opponentClass != null && ByClass.TryGetValue(opponentClass, out int[] record) ? record : new int[2];
}

// The deck stats as text (pure).
internal static class DeckStatsText
{
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A rate as a whole percent, or a dash with no games behind it.
    internal static string Rate(int wins, int games) =>
        games <= 0 ? "–" : Number((int)Math.Round(100.0 * wins / games, MidpointRounding.AwayFromZero)) + "%";

    // "12–7 · 63%", or "No games yet".
    internal static string Record(int wins, int losses) =>
        wins + losses == 0 ? "No games yet" : Number(wins) + "–" + Number(losses) + " · " + Rate(wins, wins + losses);

    // "vs Mage 3–1"; null without a class.
    internal static string Against(string opponentClass, int[] record) =>
        string.IsNullOrEmpty(opponentClass) || record == null ? null : "vs " + ClassName(opponentClass) + " " + Number(record[0]) + "–" + Number(record[1]);

    // An engine CardClass name as the game writes it: DEMONHUNTER is "Demon Hunter".
    internal static string ClassName(string engineClass)
    {
        switch (engineClass)
        {
            case null: case "": return "";
            case "DEMONHUNTER": return "Demon Hunter";
            case "DEATHKNIGHT": return "Death Knight";
            default: return engineClass.Substring(0, 1).ToUpperInvariant() + engineClass.Substring(1).ToLowerInvariant().Replace('_', ' ');
        }
    }

    // A rate's shade, from red through white to green around 50%: how HSReplay colours its win rates. Returns r, g, b.
    internal static float[] Shade(int wins, int games)
    {
        if (games <= 0) return new[] { .6f, .6f, .62f };
        float lean = Math.Max(-1f, Math.Min(1f, ((float)wins / games - .5f) / .2f));
        return lean >= 0 ? new[] { 1f - .55f * lean, 1f - .1f * lean, 1f - .55f * lean } : new[] { 1f, 1f + .55f * lean, 1f + .55f * lean };
    }
}

// The deck tracker's card art strip (CardArt), pure: the widest run of painted columns inside the strip's horizontal window.
internal static class ArtStrip
{
    // blank: one flag per column of the art, left to right, true where the column is mostly white. from/to: shares of the art's
    // width. False when no run is at least MinShare of the art wide, or nothing is blank (the window stays as it is).
    internal const float MinShare = .3f;
    internal static bool Painted(bool[] blank, float from, float to, out float paintedFrom, out float paintedTo)
    {
        paintedFrom = from; paintedTo = to;
        if (blank == null || blank.Length == 0 || to <= from) return false;
        int columns = blank.Length;
        int first = Math.Max(0, (int)Math.Ceiling(from * columns - 1e-4)), last = Math.Min(columns, (int)Math.Floor(to * columns + 1e-4));
        if (!blank.Skip(first).Take(last - first).Any(b => b)) return false;
        int bestStart = -1, bestLength = 0;
        for (int start = first; start < last;)
        {
            if (blank[start]) { start++; continue; }
            int end = start;
            while (end < last && !blank[end]) end++;
            if (end - start > bestLength) { bestStart = start; bestLength = end - start; }
            start = end;
        }
        if (bestLength < MinShare * columns) return false;
        // One column in from each white edge: the read-back is small, and the column beside white is often part white.
        int left = bestStart > first ? bestStart + 1 : bestStart, right = bestStart + bestLength < last ? bestStart + bestLength - 1 : bestStart + bestLength;
        paintedFrom = Math.Max(from, (float)left / columns); paintedTo = Math.Min(to, (float)right / columns);
        return paintedTo - paintedFrom >= MinShare;
    }
}
