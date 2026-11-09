using BepInEx.Configuration;
using UnityEngine;

// Global, read every frame: all of them apply at once. Shared by the in-match overlays through MatchOverlay: the deck tracker, the
// attack counters and the turn countdown.
internal sealed class MatchTrackerSettings
{
    private readonly ConfigEntry<bool> deckTracker, attackCounter, turnCountdown;
    private readonly ConfigEntry<KeyCode> toggle;
    internal bool DeckTracker => deckTracker.Value;
    internal bool AttackCounter => attackCounter.Value;
    internal bool TurnCountdown => turnCountdown.Value;
    internal KeyCode Toggle => toggle.Value;

    internal MatchTrackerSettings(ConfigFile file)
    {
        deckTracker = file.Bind("Display", "DeckTracker", true,
            "In a match, list the cards still in your deck, from what you have seen leave it and go into it. On by default.");
        attackCounter = file.Bind("Display", "AttackCounter", false,
            "In a match, show beside each hero how much it can still attack for this turn, or could on its next turn. Off by default.");
        turnCountdown = file.Bind("Display", "TurnCountdown", true,
            "In a match with the Turn Timer option on, show the time left in each turn above the End Turn button. On by default.");
        toggle = file.Bind("Display", "DeckTrackerKey", KeyCode.T,
            "The key that hides and shows the deck tracker, attack counters and turn countdown during a match.");
    }
}
