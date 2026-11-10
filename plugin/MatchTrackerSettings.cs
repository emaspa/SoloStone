using BepInEx.Configuration;
using UnityEngine;

// Global, read every frame: all of them apply at once. Options > Tracker Options checkboxes for the first three (NativeMatchTrackerOptions); the key is
// config only. Shared by the
// in-match overlays through MatchOverlay: the deck tracker, the attack counters and the turn countdown.
internal sealed class MatchTrackerSettings
{
    private readonly ConfigEntry<bool> deckTracker, attackCounter, turnCountdown;
    private readonly ConfigEntry<KeyCode> toggle;
    // The Options checkboxes write through these setters: each saves at once, and a failed save restores the previous value.
    internal bool DeckTracker { get => deckTracker.Value; set => Save(deckTracker, value); }
    internal bool AttackCounter { get => attackCounter.Value; set => Save(attackCounter, value); }
    internal bool TurnCountdown { get => turnCountdown.Value; set => Save(turnCountdown, value); }
    private readonly ConfigFile file;
    internal KeyCode Toggle => toggle.Value;

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
