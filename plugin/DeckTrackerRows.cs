using System;
using System.Collections.Generic;
using System.Linq;

// The deck tracker's rows, with no Unity and no engine state: every card of the decklist and every card the player saw go
// into the deck, cheapest first, with the copies the player knows are still in it. A card at 0 stays listed, greyed out.
internal sealed class DeckTrackerRow
{
    internal readonly string Id, Name;
    internal readonly int Cost, Count;
    internal readonly bool Legendary;
    internal DeckTrackerRow(string id, string name, int cost, int count, bool legendary) { Id = id; Name = name; Cost = cost; Count = count; Legendary = legendary; }
}

internal sealed class DeckTrackerView
{
    internal readonly IReadOnlyList<DeckTrackerRow> Rows;
    // Unknown: cards in the deck the player can't name. UnseenRemoved: cards that left without the player seeing which.
    internal readonly int Remaining, Unknown, UnseenRemoved;
    internal DeckTrackerView(IReadOnlyList<DeckTrackerRow> rows, int remaining, int unknown, int unseenRemoved)
    { Rows = rows; Remaining = remaining; Unknown = unknown; UnseenRemoved = unseenRemoved; }

    internal string Summary => "remaining=" + Remaining + " unknown=" + Unknown + " unseenRemoved=" + UnseenRemoved
        + " cards=" + string.Join(",", Rows.Where(r => r.Count != 0).Select(r => r.Id + "x" + r.Count));
}

internal static class DeckTrackerRows
{
    internal struct Card { internal string Name; internal int Cost; internal bool Legendary; }

    internal static DeckTrackerView Build(IEnumerable<string> decklist, IReadOnlyDictionary<string, int> known, int unknown, int unseenRemoved,
        int remaining, Func<string, Card> describe)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in decklist.Concat(known.Keys)) if (seen.Add(id)) ids.Add(id);
        var rows = ids.Select(id => { var card = describe(id); return new DeckTrackerRow(id, card.Name, card.Cost, known.TryGetValue(id, out int count) ? count : 0, card.Legendary); })
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        return new DeckTrackerView(rows, remaining, unknown, unseenRemoved);
    }

    // Before the first observation: the decklist the player chose.
    internal static DeckTrackerView Decklist(IReadOnlyList<string> decklist, Func<string, Card> describe) =>
        Build(decklist, decklist.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            0, 0, decklist.Count, describe);
}

// The opponent tracker's rows, with no Unity and no engine state: the opponent's cards the player has seen, those from their starting
// deck and those created in the match, each cheapest first with how many copies were seen. Hand and Deck are the opponent's public
// card counts.
internal sealed class OpponentTrackerView
{
    internal readonly IReadOnlyList<DeckTrackerRow> Deck, Created;
    internal readonly int Hand, Remaining;
    internal OpponentTrackerView(IReadOnlyList<DeckTrackerRow> deck, IReadOnlyList<DeckTrackerRow> created, int hand, int remaining)
    { Deck = deck; Created = created; Hand = hand; Remaining = remaining; }

    internal int Seen => Deck.Sum(r => r.Count);

    internal string Summary => "hand=" + Hand + " deck=" + Remaining + " seen=" + string.Join(",", Deck.Select(r => r.Id + "x" + r.Count))
        + " created=" + string.Join(",", Created.Select(r => r.Id + "x" + r.Count));
}

internal static class OpponentTrackerRows
{
    // fromDeck: one id per starting card the player has seen; created: one id per created card the opponent played.
    internal static OpponentTrackerView Build(IEnumerable<string> fromDeck, IEnumerable<string> created, int hand, int remaining,
        Func<string, DeckTrackerRows.Card> describe) =>
        new OpponentTrackerView(Rows(fromDeck, describe), Rows(created, describe), hand, remaining);

    private static IReadOnlyList<DeckTrackerRow> Rows(IEnumerable<string> ids, Func<string, DeckTrackerRows.Card> describe) =>
        (ids ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).GroupBy(id => id, StringComparer.Ordinal)
            .Select(group => { var card = describe(group.Key); return new DeckTrackerRow(group.Key, card.Name, card.Cost, group.Count(), card.Legendary); })
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
}
