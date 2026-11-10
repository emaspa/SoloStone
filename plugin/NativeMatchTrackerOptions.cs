using System;
using BepInEx.Logging;
using UnityEngine;

// Options > Tracker Options: checkboxes for the match overlays (MatchOverlay), Countdown, Deck Tracker and Attack Counter
// (NativeOptionsPanels builds the rows and hands each to Attach). Each applies at once and persists in the BepInEx config ([Display]
// TurnCountdown, DeckTracker, AttackCounter). The countdown only runs while the Turn Timer does, and its label says so
// (OptionsPanelText.CountdownLabel). Every path fails closed: a failed save logs one warning and the box shows the saved value.
internal static class NativeMatchTrackerOptions
{
    private sealed class Row
    {
        internal string Key;
        internal Func<bool> Get;
        internal Action<bool> Set;
        internal CheckBox Box;
    }

    private static Row[] rows;
    private static ManualLogSource log;

    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        var settings = MatchOverlay.Settings;
        rows = new[] {
            new Row { Key = "TurnCountdown", Get = () => settings.TurnCountdown, Set = value => settings.TurnCountdown = value },
            new Row { Key = "DeckTracker", Get = () => settings.DeckTracker, Set = value => settings.DeckTracker = value },
            new Row { Key = "AttackCounter", Get = () => settings.AttackCounter, Set = value => settings.AttackCounter = value },
        };
    }

    // The panel's row for setting `index` (0 Countdown, 1 Deck Tracker, 2 Attack Counter), built once; it stays for the session.
    internal static void Attach(int index, CheckBox box)
    {
        if (rows == null || MatchOverlay.Settings == null) throw new InvalidOperationException("match tracker settings unavailable");
        var row = rows[index];
        box.AddEventListener(UIEventType.RELEASE, _ => {
            try { row.Set(box.IsChecked()); log.LogInfo("MATCH_TRACKER_OPTION " + row.Key + "=" + row.Get() + " applies=now"); }
            catch (Exception error) { log.LogWarning("MATCH_TRACKER_OPTION " + row.Key + " not saved: " + error.Message); }
            box.SetChecked(row.Get());
        });
        row.Box = box;
        box.SetChecked(row.Get());
    }

    internal static void Sync()
    {
        try { if (rows != null) foreach (var row in rows) if (row.Box != null) row.Box.SetChecked(row.Get()); }
        catch (Exception error) { log?.LogWarning("MATCH_TRACKER_OPTION sync: " + error.Message); }
    }

    internal static CheckBox Box(int index) => rows == null || index >= rows.Length ? null : rows[index].Box;
}
