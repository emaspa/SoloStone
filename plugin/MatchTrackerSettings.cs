using BepInEx.Configuration;
using UnityEngine;

// Global, read every frame: all of them apply at once. Options > Tracker Options has a checkbox for each switch (NativeMatchTrackerOptions);
// the keys are config only. Shared by the in-match overlays through MatchOverlay: the deck tracker and the opponent tracker (with its card art, deck stats and
// mulligan guide), the card chips, the secret helper, the attack counters and the turn countdown.
internal sealed class MatchTrackerSettings
{
    private readonly ConfigEntry<bool> deckTracker, attackCounter, turnCountdown, cardArt, deckStats, mulliganGuide, opponentTracker, cardChips, secretHelper, counterChips, cardHistory;
    private readonly ConfigEntry<KeyCode> toggle, statsKey;
    // The Options checkboxes write through these setters: each saves at once, and a failed save restores the previous value.
    internal bool DeckTracker { get => deckTracker.Value; set => Save(deckTracker, value); }
    internal bool AttackCounter { get => attackCounter.Value; set => Save(attackCounter, value); }
    internal bool TurnCountdown { get => turnCountdown.Value; set => Save(turnCountdown, value); }
    internal bool CardArt { get => cardArt.Value; set => Save(cardArt, value); }
    internal bool DeckStats { get => deckStats.Value; set => Save(deckStats, value); }
    internal bool MulliganGuide { get => mulliganGuide.Value; set => Save(mulliganGuide, value); }
    internal bool OpponentTracker { get => opponentTracker.Value; set => Save(opponentTracker, value); }
    internal bool CardChips { get => cardChips.Value; set => Save(cardChips, value); }
    internal bool SecretHelper { get => secretHelper.Value; set => Save(secretHelper, value); }
    internal bool CounterChips { get => counterChips.Value; set => Save(counterChips, value); }
    internal bool CardHistory { get => cardHistory.Value; set => Save(cardHistory, value); }
    // Any of the overlays that need the match's game: all but the turn countdown.
    internal bool AnyTracker => DeckTracker || AttackCounter || OpponentTracker || CardChips || SecretHelper || CounterChips || CardHistory;
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
        opponentTracker = file.Bind("Display", "OpponentTracker", true,
            "In a match, list the opponent's cards you have seen: played, summoned, revealed or discarded from their deck, and the ones they created. On by default. Also in Options > Tracker Options.");
        cardChips = file.Bind("Display", "CardChips", true,
            "In a match, put a chip under each card in the opponent's hand: a letter for how it came (T drawn at the start of a turn, D drawn by a card, C created, R returned, K kept, M from the mulligan) and the round, red when it is Temporary. Hover a chip for more. On by default. Also in Options > Tracker Options (Hand Chips).");
        counterChips = file.Bind("Display", "CounterChips", true,
            "In a match, put a chip on a hero power with how many times it was Imbued, and beside a quest or other counter with its progress. On by default. Also in Options > Tracker Options.");
        cardHistory = file.Bind("Display", "CardHistory", true,
            "In a match, hovering a card that resurrects minions, recasts spells or repeats Battlecries shows the cards it would draw on, at 40% size. On by default. Also in Options > Tracker Options.");
        secretHelper = file.Bind("Display", "SecretHelper", true,
            "In a match, while the opponent has a hidden Secret, list the Secrets their class could have beside their hero and dim the ones your plays have ruled out. Hover a name for its text. On by default. Also in Options > Tracker Options.");
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
