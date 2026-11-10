using System.Collections.Generic;

// Card chips: small labels the match overlays put on cards (NativeCardChips finds the facts and draws them). Pure: no Unity or
// engine types, so the launcher tests run it.
// Under each card in the opponent's hand, a letter and the round it came: T drawn at the start of a turn, D drawn by a card, C created,
// R returned to hand; K kept and M got in the mulligan; Coin. Its colour says how, red when it is Temporary. On a hero power: how many times it was Imbued. On a
// quest, sidequest or other counter beside a hero: its progress. Hovering a chip shows its detail.
internal enum HandOrigin { Kept, Mulligan, Coin, Drawn, Created, Returned, Other }

// What the player has seen of one card in the opponent's hand.
internal sealed class HandFacts
{
    internal HandOrigin Origin;
    // The game turn it entered the hand (both players' turns count); 0 for the opening hand.
    internal int Turn;
    // The card that drew, created or returned it, when the player saw that card; else null.
    internal string Source;
    // The card itself, when the player saw it before it went to hand (a returned minion); else null.
    internal string Known;
    internal bool Temporary;
}

internal enum ChipTag { Created, Drawn, Returned, Temporary }

internal sealed class Chip
{
    internal string Label;
    internal IReadOnlyList<ChipTag> Tags = new ChipTag[0];
    internal string Detail;
}

internal static class CardChips
{
    // A game turn as both players count it: turn 1 and 2 are the first round, and so on.
    internal static int Round(int turn) => turn <= 0 ? 0 : (turn + 1) / 2;

    internal static string TagText(ChipTag tag) =>
        tag == ChipTag.Created ? "Created" : tag == ChipTag.Drawn ? "Drawn" : tag == ChipTag.Returned ? "Returned" : "Temporary";

    internal static Chip Hand(HandFacts card)
    {
        if (card == null) return null;
        var tags = new List<ChipTag>();
        var lines = new List<string>();
        string when = card.Turn > 0 ? "on turn " + Round(card.Turn) : "at the start of the game";
        // One letter for how, then the round: T3 drawn at the start of their turn 3, D drawn by a card, C created, R returned.
        int round = Round(card.Turn);
        string label = "T" + round;
        string by = card.Source == null ? "" : " by " + card.Source;
        switch (card.Origin)
        {
            case HandOrigin.Kept: label = "K"; lines.Add("In their opening hand, kept in the mulligan."); break;
            case HandOrigin.Mulligan: label = "M"; lines.Add("Got in the mulligan."); break;
            case HandOrigin.Coin: label = "Coin"; lines.Add("The Coin, for going second."); break;
            case HandOrigin.Drawn:
                if (card.Source != null) { tags.Add(ChipTag.Drawn); label = "D" + round; }
                lines.Add("Drawn " + when + by + ".");
                break;
            case HandOrigin.Created: tags.Add(ChipTag.Created); label = "C" + round; lines.Add("Created " + when + by + "."); break;
            case HandOrigin.Returned:
                tags.Add(ChipTag.Returned); label = "R" + round;
                lines.Add("Returned to hand " + when + by + ".");
                if (card.Known != null) lines.Add("It is " + card.Known + ".");
                break;
            default: lines.Add("Came to hand " + when + by + "."); break;
        }
        if (card.Temporary) { tags.Add(ChipTag.Temporary); lines.Add("Temporary: it leaves their hand at the end of the turn."); }
        return new Chip { Label = label, Tags = tags, Detail = string.Join("\n", lines) };
    }

    internal static Chip HeroPower(int imbued) => imbued <= 0 ? null
        : new Chip { Label = "Imbue " + imbued, Detail = "Hero Power Imbued " + imbued + (imbued == 1 ? " time." : " times.") };

    internal static Chip Quest(string name, int progress, int total) => total <= 0 ? null
        : new Chip { Label = progress + "/" + total, Detail = (string.IsNullOrEmpty(name) ? "Progress" : name) + ": " + progress + " of " + total + "." };

    // One line for the log: the chips by entity, in entity order.
    internal static string Summary(IDictionary<int, Chip> chips)
    {
        var ids = new List<int>(chips.Keys);
        ids.Sort();
        var parts = new List<string>();
        foreach (int id in ids)
        {
            var chip = chips[id];
            string tags = "";
            foreach (var tag in chip.Tags) tags += "+" + TagText(tag);
            parts.Add(id + ":" + chip.Label + tags);
        }
        return string.Join(" ", parts);
    }
}
