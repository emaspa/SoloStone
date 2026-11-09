using System;
using System.Collections.Generic;

// Options > SoloStone Options and Tracker Options (NativeOptionsPanels): ids, copy and the layout arithmetic. Pure (no Unity types),
// so the launcher tests check it. Coordinates are distances along the screen's right and up axes (y grows upward), the space
// ProfileFormKit.Span measures in; a rect's X/Y is its left/bottom corner.
internal static class OptionsPanelText
{
    internal const string SoloStoneKey = "solostone", TrackerKey = "tracker";
    internal const string SoloStoneButton = "SoloStone Options", TrackerButton = "Tracker Options";
    internal const string SoloStoneTitle = "SoloStone Options", TrackerTitle = "Tracker Options";
    internal const string SoloStoneButtonId = "options.solostone", TrackerButtonId = "options.tracker";
    internal const string SoloStoneButtonObject = "SoloStoneOptionsButton", TrackerButtonObject = "SoloStoneTrackerOptionsButton";
    internal const string HandheldId = "options.handheld", CountdownId = "options.countdown", DeckTrackerId = "options.deck-tracker",
        AttackCounterId = "options.attack-counter", CardArtId = "options.card-art", DeckStatsId = "options.deck-stats",
        MulliganGuideId = "options.mulligan-guide";
    // The countdown only runs while the Turn Timer does (NativeTurnClock), so its label says so.
    internal const string CountdownLabel = "Countdown\n(with Turn Timer)", DeckTrackerLabel = "Deck Tracker", AttackCounterLabel = "Attack Counter";
    internal const string CardArtLabel = "Card Art", DeckStatsLabel = "Deck Stats", MulliganGuideLabel = "Mulligan Guide";
    internal static string ButtonId(string panel) => panel == SoloStoneKey ? SoloStoneButtonId : TrackerButtonId;
    internal static string BackId(string panel) => ButtonId(panel) + ".back";
    internal static string DoneId(string panel) => ButtonId(panel) + ".done";
    internal static string DismissId(string panel) => ButtonId(panel) + ".dismiss";
    internal static string Title(string panel) => panel == SoloStoneKey ? SoloStoneTitle : TrackerTitle;
}

internal struct PanelRect
{
    internal float X, Y, Width, Height;
    internal PanelRect(float x, float y, float width, float height) { X = x; Y = y; Width = width; Height = height; }
    internal float Top => Y + Height;
    internal float CenterX => X + Width / 2;
    internal float CenterY => Y + Height / 2;
    internal static bool Overlap(PanelRect a, PanelRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    internal bool Contains(PanelRect inner) => inner.X >= X && inner.Top <= Top && inner.Y >= Y && inner.X + inner.Width <= X + Width;
}

// One control in a panel column: its unscaled rendered size, and whether it lines up on the column's left edge (checkboxes, so their
// boxes form one line) or sits centred (planks).
internal struct PanelRow
{
    internal float Width, Height;
    internal bool AlignLeft;
    internal PanelRow(float width, float height, bool alignLeft) { Width = width; Height = height; AlignLeft = alignLeft; }
}

internal sealed class PanelPlan
{
    internal float Scale;
    internal PanelRect[][] Rows;
}

internal static class OptionsPanelLayout
{
    // Share of the frame kept clear inside its edges, and between the header or the button bar and the first or last row.
    internal const float SideInset = .07f, EdgeGap = .04f;
    // A row fills at most this share of its pitch, and of its column's width; a pitch is at most this many row heights.
    internal const float RowFill = .86f, ColumnFill = .94f, MaxPitchRows = 1.9f;

    // The free area of a panel: inside the frame, under the header, over the button bar.
    internal static PanelRect Region(PanelRect frame, float headerBottom, float barTop)
    {
        float top = Math.Min(frame.Top, headerBottom) - frame.Height * EdgeGap;
        float bottom = Math.Max(frame.Y, barTop) + frame.Height * EdgeGap;
        float inset = frame.Width * SideInset;
        return new PanelRect(frame.X + inset, bottom, Math.Max(0, frame.Width - 2 * inset), Math.Max(0, top - bottom));
    }

    // Columns side by side across the region, each a top-down list of rows at one shared pitch, the block centred vertically. Rows
    // shrink together (Scale < 1) when the tallest would crowd its pitch or the widest its column; they never grow.
    internal static PanelPlan Plan(PanelRect region, IList<IList<PanelRow>> columns)
    {
        if (columns == null || columns.Count == 0 || region.Width <= 0 || region.Height <= 0) throw new InvalidOperationException("nothing to lay out");
        int most = 0; float tallest = 0;
        foreach (var column in columns)
        {
            most = Math.Max(most, column.Count);
            foreach (var row in column) tallest = Math.Max(tallest, row.Height);
        }
        if (most == 0 || tallest <= 0) throw new InvalidOperationException("rows have no size");
        float columnWidth = region.Width / columns.Count;
        float scale = Math.Min(1f, region.Height / most * RowFill / tallest);
        foreach (var column in columns)
        {
            float widest = 0;
            foreach (var row in column) widest = Math.Max(widest, row.Width);
            if (widest > 0) scale = Math.Min(scale, columnWidth * ColumnFill / widest);
        }
        float pitch = Math.Min(region.Height / most, tallest * scale * MaxPitchRows);
        float top = region.Top - (region.Height - pitch * most) / 2;
        var plan = new PanelPlan { Scale = scale, Rows = new PanelRect[columns.Count][] };
        for (int c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            float left = region.X + c * columnWidth, widest = 0;
            foreach (var row in column) widest = Math.Max(widest, row.Width * scale);
            plan.Rows[c] = new PanelRect[column.Count];
            for (int i = 0; i < column.Count; i++)
            {
                float width = column[i].Width * scale, height = column[i].Height * scale, middle = top - pitch * (i + .5f);
                float x = column[i].AlignLeft ? left + (columnWidth - widest) / 2 : left + (columnWidth - width) / 2;
                plan.Rows[c][i] = new PanelRect(x, middle - height / 2, width, height);
            }
        }
        return plan;
    }

    // Options' Other group: where the two planks may go, best first. `shake` is Disable Screen Shake, `above` the hidden spectator
    // row over it (its native slot), `slot` the hidden Miscellaneous plank under it (null when the client has none), `plank` the
    // size of one of our planks. "below": both under Screen Shake, the first in Miscellaneous's slot. "around": the first in the
    // spectator slot over Screen Shake, the second in Miscellaneous's slot. A plank never comes closer to Screen Shake than a
    // tenth of its height, and the two are a quarter of a height apart.
    internal const float ShakeGap = .1f, PlankGap = .25f;
    internal static List<KeyValuePair<string, PanelRect[]>> ButtonSlots(PanelRect shake, PanelRect above, PanelRect? slot, float plankWidth, float plankHeight)
    {
        float x = (slot ?? shake).CenterX - plankWidth / 2;
        float under = Math.Min(slot?.CenterY ?? shake.Y - plankHeight, shake.Y - plankHeight * ShakeGap - plankHeight / 2);
        float over = Math.Max(above.CenterY, shake.Top + plankHeight * ShakeGap + plankHeight / 2);
        PanelRect At(float middle) => new PanelRect(x, middle - plankHeight / 2, plankWidth, plankHeight);
        return new List<KeyValuePair<string, PanelRect[]>> {
            new KeyValuePair<string, PanelRect[]>("below", new[] { At(under), At(under - plankHeight * (1 + PlankGap)) }),
            new KeyValuePair<string, PanelRect[]>("around", new[] { At(over), At(under) })
        };
    }
}
