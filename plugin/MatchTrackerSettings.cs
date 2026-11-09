using BepInEx.Configuration;
using UnityEngine;

// Global, read every frame: all of them apply at once. Options > Tracker Options has a checkbox for each switch (NativeMatchTrackerOptions);
// the keys are config only. Shared by the in-match overlays through MatchOverlay: the deck tracker (with its card art, deck stats and
// mulligan guide), the attack counters and the turn countdown.
internal sealed class MatchTrackerSettings
{
    private readonly ConfigEntry<bool> deckTracker, attackCounter, turnCountdown, cardArt, deckStats, mulliganGuide;
    private readonly ConfigEntry<KeyCode> toggle, statsKey;
    // The Options checkboxes write through these setters: each saves at once, and a failed save restores the previous value.
    internal bool DeckTracker { get => deckTracker.Value; set => Save(deckTracker, value); }
    internal bool AttackCounter { get => attackCounter.Value; set => Save(attackCounter, value); }
    internal bool TurnCountdown { get => turnCountdown.Value; set => Save(turnCountdown, value); }
    internal bool CardArt { get => cardArt.Value; set => Save(cardArt, value); }
    internal bool DeckStats { get => deckStats.Value; set => Save(deckStats, value); }
    internal bool MulliganGuide { get => mulliganGuide.Value; set => Save(mulliganGuide, value); }
    private readonly ConfigFile file;
    internal KeyCode Toggle => toggle.Value;
    internal KeyCode StatsKey => statsKey.Value;

    internal MatchTrackerSettings(ConfigFile file)
    {
        this.file = file;
        deckTracker = file.Bind("Display", "DeckTracker", true,
            "In a match, list the cards still in your deck, from what you have seen leave it and go into it. On by default. Also in Options > Tracker Options.");
        attackCounter = file.Bind("Display", "AttackCounter", false,
            "In a match, show beside each hero how much it can still attack for this turn, or could on its next turn. Off by default. Also in Options > Tracker Options.");
        turnCountdown = file.Bind("Display", "TurnCountdown", true,
            "In a match with the Turn Timer option on, show the time left in each turn above the End Turn button. On by default. Also in Options > Tracker Options (Countdown).");
        toggle = file.Bind("Display", "DeckTrackerKey", KeyCode.T,
            "The key that hides and shows the deck tracker, attack counters and turn countdown during a match.");
        cardArt = file.Bind("Display", "CardArt", true,
            "Show each card's art in the deck tracker's rows. On by default. Also in Options > Tracker Options.");
        deckStats = file.Bind("Display", "DeckStats", true,
            "Show the deck's record under the deck tracker's title, and let the stats key add each card's win rates. On by default. Also in Options > Tracker Options.");
        mulliganGuide = file.Bind("Display", "MulliganGuide", true,
            "During the mulligan, show under each card how often you kept it and your win rate with it in your opening hand. On by default. Also in Options > Tracker Options.");
        statsKey = file.Bind("Display", "DeckStatsKey", KeyCode.S,
            "The key that shows and hides the card win rates in the deck tracker during a match.");
    }

    private void Save(ConfigEntry<bool> entry, bool value)
    {
        bool previous = entry.Value, automatic = file.SaveOnConfigSet;
        try
        {
            file.SaveOnConfigSet = false;
            entry.Value = value;
            try { file.Save(); }
            catch { entry.Value = previous; throw; }
        }
        finally { file.SaveOnConfigSet = automatic; }
    }
}
