using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using static MatchOverlay;

// The match tracker, IMGUI overlays during a match. The deck tracker: one panel down the right edge, under the brand label,
// listing the cards still in the player's deck (DeckTrackerRows), each row over the card's art from the game's own deck lists
// (CardArt). It reads only what the player has seen: OwnDeckLedger's observations of the player's own side. With Deck Stats on,
// the deck's record from the profile sits under the title, and the stats key adds each card's win rates (DeckStats); during the
// mulligan the mulligan guide shows under each offered card how often it was kept and the win rate with it in the opening hand.
// The attack counters (off by default): a number beside each hero (AttackCounter), from the public board. The list and the
// counters refresh only when the native board has settled (ClientBridge.BoardSettled), so a draw never leaves the list, and an
// attack never changes a counter, before its animation shows it. Before the first refresh the deck tracker shows the chosen
// decklist. None takes input or changes game state; the toggle key (MatchOverlay) only hides them. Every path fails closed: an
// error disables them for the session with one warning.
// The opponent tracker: a panel down the left edge listing the opponent's cards the player has seen (OpponentTrackerRows), from the
// engine's history as the player's fair view shows it (HistoryFacts.ProjectForFairView, the view the NPC plays from, here for the
// player's side): played, summoned, discarded and otherwise revealed cards from their starting deck, then the cards they created and
// played. A Secret stays hidden until it is revealed; nothing in their hand or deck shows. Under the name: their hand and deck
// counts. It fails closed on its own: an error turns only the opponent tracker off for the session.
internal static class NativeMatchTracker
{
    private static MatchTrackerSettings settings;
    private static ManualLogSource log;
    private static bool failed;
    private static NativeMatch tracked;
    private static int observedHistory = -1;
    // Which trackers the last refresh filled: a tracker switched on mid-match must not wait for the next engine event.
    private static int observedTrackers;
    private static string lastSummary;
    private static DeckTrackerView view;
    // The deck's stats over its earlier matches (this one is still pending), and the line under the title.
    private static DeckStats stats;
    private static string recordLine;
    // The attack counters: friendly and opposing, and where each hero is on screen (GUI coordinates, empty when unknown).
    private static int friendlyAttack = -1, opposingAttack = -1;
    private static Rect friendlyHero, opposingHero;
    private static int heroesFrame = -100;
    // The opponent tracker: what the player has seen of the opponent's cards, and the panel's header.
    private static OpponentTrackerView opponent;
    private static bool opponentFailed;
    private static string opponentName = "Opponent", opponentTitle = "Opponent", opponentClass = "";
#if ENGINE_SOURCE
    private static OwnDeckLedger ledger;
#endif

    internal static void Initialize(ManualLogSource logger) { log = logger; settings = MatchOverlay.Settings; }

    // Called from OfflineProbe.Update, once per frame.
    internal static void Tick()
    {
        if (failed || settings == null) return;
        try
        {
            var match = ClientBridge.CurrentMatch;
            var game = match?.Game;
            if (!settings.DeckTracker && !settings.AttackCounter && !settings.OpponentTracker || game == null || !MatchOverlay.InMatch()) { Forget(); return; }
            if (!ReferenceEquals(match, tracked))
            {
                Forget(); tracked = match;
                deckName = string.IsNullOrWhiteSpace(match.PlayerDeckName) ? "Deck" : match.PlayerDeckName.Trim();
                view = DeckTrackerRows.Decklist(game.Player1.DeckCards.Select(c => c.Id).ToList(), Describe);
                LoadStats(game.Player2?.Hero?.Card?.Class.ToString());
                opponentName = string.IsNullOrWhiteSpace(match.OpponentName) ? "Opponent" : match.OpponentName.Trim();
                opponentClass = DeckStatsText.ClassName(game.Player2?.Hero?.Card?.Class.ToString());
                opponent = OpponentTrackerRows.Build(null, null, game.Player2?.HandZone?.Count ?? 0, game.Player2?.DeckZone?.Count ?? 0, Describe);
            }
#if ENGINE_SOURCE
            if (settings.AttackCounter && Time.frameCount - heroesFrame >= 30) { heroesFrame = Time.frameCount; PlaceHeroes(); }
            int history = game.PowerHistory?.Full?.Count ?? 0;
            int trackers = (settings.DeckTracker ? 1 : 0) | (settings.OpponentTracker ? 2 : 0);
            if (trackers != observedTrackers) { observedTrackers = trackers; observedHistory = -1; }
            if (history == observedHistory || !ClientBridge.BoardSettled()) return;
            bool friendlyTurn = game.CurrentPlayer == game.Player1;
            friendlyAttack = AttackCounter.For(game.Player1, friendlyTurn);
            opposingAttack = AttackCounter.For(game.Player2, !friendlyTurn);
            if (settings.DeckTracker)
            {
                ledger = OwnDeckLedger.Observe(game, game.Player1, ledger, shared: false);
                var known = ledger.Track(game, game.Player1);
                view = DeckTrackerRows.Build(game.Player1.DeckCards.Select(c => c.Id), known.Known, known.Unknown, known.UnseenRemoved, known.Remaining, Describe);
            }
            if (settings.OpponentTracker && !opponentFailed) ObserveOpponent(game, match);
            observedHistory = history;
            string summary = view?.Summary ?? "off";
            summary += " attack=" + friendlyAttack + "/" + opposingAttack;
            if (summary != lastSummary) { lastSummary = summary; log.LogInfo("DECK_TRACKER " + summary); }
            string seen = settings.OpponentTracker ? opponent?.Summary : null;
            if (seen != null && seen != lastOpponent) { lastOpponent = seen; log.LogInfo("OPPONENT_TRACKER " + seen); }
#endif
        }
        catch (Exception error) { Fail(error); }
    }

#if ENGINE_SOURCE
    private static string lastOpponent;

    // The opponent's cards as the player's fair view of the history shows them. The fair view keeps a fact's card only when the
    // player saw it, and hides the entity of a card now in a hidden zone; the same fact in the full history (matched by sequence)
    // gives the entity, which says whether the card was in the opponent's starting deck. Entity ids are public in the client too.
    private static void ObserveOpponent(SabberStoneCore.Model.Game game, NativeMatch match)
    {
        try
        {
            var them = game.Player2;
            var full = new Dictionary<long, SabberStoneCore.Model.HistoryCardFact>();
            foreach (var fact in game.HistoryFacts.Query.Events<SabberStoneCore.Model.HistoryCardFact>()) full[fact.Sequence] = fact;
            var fair = game.HistoryFacts.ProjectForFairView(game, game.Player1.PlayerId);
            var revealed = new Dictionary<int, string>();
            var created = new Dictionary<int, string>(); // by entity: a created card played twice is still one card
            foreach (var fact in fair.Query.Events<SabberStoneCore.Model.HistoryCardFact>())
            {
                if (!fact.DefinitionKnown || !full.TryGetValue(fact.Sequence, out var source)) continue;
                var card = SabberStoneCore.Model.Cards.FromAssetId(fact.DbfId);
                if (card == null || card.Id == "GAME_005" || card.Type == SabberStoneCore.Enums.CardType.HERO_POWER
                    || card.Type == SabberStoneCore.Enums.CardType.ENCHANTMENT) continue;
                if (match.OpponentStartedWith(source.EntityId)) { if (!revealed.ContainsKey(source.EntityId)) revealed[source.EntityId] = card.Id; }
                else if (fact is SabberStoneCore.Model.CardPlayedFact && fact.ControllerId == them.Id && !created.ContainsKey(source.EntityId)) created[source.EntityId] = card.Id;
            }
            opponent = OpponentTrackerRows.Build(revealed.Values, created.Values, them.HandZone.Count, them.DeckZone.Count, Describe);
        }
        catch (Exception error)
        {
            opponentFailed = true; opponent = null;
            log?.LogWarning("OPPONENT_TRACKER off for the session: " + error);
        }
    }
#endif

    // The deck's record and card rates from the profile's settled matches. A profile that can't answer leaves the tracker without
    // stats, not off.
    private static void LoadStats(string opponentClass)
    {
        stats = null; recordLine = null;
        try
        {
            var profile = LocalLadder.Profile;
            string deck = profile?.Pending?.Deck;
            if (profile == null || deck == null) return;
            stats = profile.DeckStatsFor(deck);
            string against = DeckStatsText.Against(opponentClass, stats.Against(opponentClass));
            recordLine = DeckStatsText.Record(stats.Wins, stats.Losses) + (against == null ? "" : "   " + against);
            log.LogInfo("DECK_STATS deck=" + deck + " record=" + stats.Wins + "-" + stats.Losses + " tracked=" + stats.TrackedGames
                + " cards=" + stats.Cards.Count + " opponent=" + opponentClass);
        }
        catch (Exception error) { stats = null; recordLine = null; log.LogWarning("DECK_STATS unavailable for this match: " + error.Message); }
    }

    private static void Forget()
    {
        if (tracked != null) CardArt.Clear();
        tracked = null; view = null; observedHistory = -1; lastSummary = null; measuredView = null; drawnWidth = 0f; deckName = title = "Deck";
        stats = null; recordLine = null;
        opponent = null; opponentName = opponentTitle = "Opponent"; opponentClass = ""; measuredOpponent = null; opponentWidth = 0f;
#if ENGINE_SOURCE
        lastOpponent = null;
#endif
        friendlyAttack = opposingAttack = -1; friendlyHero = opposingHero = Rect.zero; heroesFrame = -100;
#if ENGINE_SOURCE
        ledger = null;
#endif
    }

    // Each hero portrait's rendered bounds, flipped to GUI coordinates (top-left origin).
    private static void PlaceHeroes()
    {
        var state = GameState.Get();
        friendlyHero = HeroRect(state?.GetFriendlySidePlayer()); opposingHero = HeroRect(state?.GetOpposingSidePlayer());
    }

    private static Rect HeroRect(Player player)
    {
        var actor = player?.GetHero()?.GetCard()?.GetActor();
        if (actor == null || !actor.gameObject.activeInHierarchy) return Rect.zero;
        // The portrait mesh only: the whole actor's renderers reach far beyond it (the friendly hero's measured to mid-board).
        return MatchOverlay.GuiRect(actor.GetMeshRenderer(), actor.gameObject.layer);
    }

    private static DeckTrackerRows.Card Describe(string id)
    {
        var card = SabberStoneCore.Model.Cards.FromId(id);
        return new DeckTrackerRows.Card { Name = card?.Name ?? id, Cost = card?.Cost ?? 0, Legendary = card?.Rarity == SabberStoneCore.Enums.Rarity.LEGENDARY };
    }

    private static void Fail(Exception error)
    {
        failed = true; view = null; friendlyAttack = opposingAttack = -1;
        try { CardArt.Clear(); } catch (Exception) { }
        log?.LogWarning("MATCH_TRACKER disabled for the session: " + error);
    }

    // Drawing. Sizes are shares of the screen height, like the brand label; rows shrink to fit a long list.
    private static GUIStyle name, number, header, note, small;
    private static int styleRow;
    private static Texture2D panel, badge, zeroBadge, attackPanel, fade, tile, countBox;
    internal static readonly Color PanelColor = new Color(.05f, .06f, .09f, .78f);
    // Rows are tiles a little taller than a line of text, so the card art reads; a long list shrinks them to fit.
    internal const float RowShare = .034f, WidthRows = 8f, MaxWidthRows = 11.5f, TopShare = .075f, BottomShare = .9f, MarginShare = .012f;
    // Text and the stats header are sized from this share of the screen, not from the taller tiles.
    internal const float TextShare = .026f;
    // Card art fills the right part of a tile, between the name's start and the count box, fading out towards the cost badge; the
    // stats columns are this many text lines wide.
    internal const float ArtStart = .28f, FadeShare = .5f, ColumnRows = 2.4f, StatsLine = .9f, TileGap = .08f;
    internal static readonly Color TileColor = new Color(.08f, .09f, .12f, 1f);
    internal static readonly string[] ColumnNames = { "Mulligan", "Drawn", "Played" };
    // The panel is as wide as its longest name needs, between WidthRows and MaxWidthRows rows; measured once per list and size.
    private static DeckTrackerView measuredView;
    private static int measuredRow;
    private static float nameWidth, drawnWidth;
    internal static float PanelWidth(int height) => drawnWidth > 0 ? drawnWidth : height * RowShare * WidthRows;
    // The header names the chosen deck; a name too long for the widest panel ends in an ellipsis.
    private static string deckName = "Deck", title = "Deck";

    private static string Fit(string text, float width, GUIStyle style)
    {
        if (Measure(text, style).x <= width) return text;
        for (int length = text.Length - 1; length > 0; length--)
        {
            string cut = text.Substring(0, length).TrimEnd() + "…";
            if (Measure(cut, style).x <= width) return cut;
        }
        return "…";
    }

    // Called from OfflineProbe.OnGUI. Repaint only: it takes no input.
    internal static void Draw()
    {
        if (failed || MatchOverlay.Hidden || Event.current.type != EventType.Repaint) return;
        try
        {
            if (settings.AttackCounter) { Counter(friendlyHero, friendlyAttack); Counter(opposingHero, opposingAttack); }
            if (settings.OpponentTracker && !opponentFailed) DrawOpponent();
            if (!settings.DeckTracker || view == null) return;
            bool statsShown = settings.DeckStats && stats != null, columns = statsShown && MatchOverlay.StatsShown;
            if (statsShown && settings.MulliganGuide) MulliganGuide();
            int height = Screen.height, width = Screen.width;
            int lines = view.Rows.Count + (view.Unknown > 0 ? 1 : 0);
            float extra = (statsShown ? StatsLine : 0f) + (columns ? StatsLine * 2 : 0f) + (view.UnseenRemoved > 0 ? 1f : 0f);
            float top = height * TopShare, available = height * BottomShare - top;
            // `line` sizes text and the header lines, `row` the card tiles; a long list shrinks the tiles first, then the text.
            float line = height * TextShare;
            float row = Mathf.Min(height * RowShare, (available - line * (1.6f + extra)) / Mathf.Max(1, lines));
            line = Mathf.Min(line, row);
            if (row < height * TextShare * .75f) { row = available / (lines + 1.6f + extra); line = row; }
            Styles(Mathf.Max(8, Mathf.RoundToInt(line)));
            float pad = line * .35f;
            string remaining = view.Remaining + (view.Remaining == 1 ? " card" : " cards");
            if (!ReferenceEquals(measuredView, view) || measuredRow != styleRow)
            {
                nameWidth = view.Unknown > 0 ? Measure("Unknown card", name).x : 0f;
                foreach (var entry in view.Rows) nameWidth = Mathf.Max(nameWidth, Measure(entry.Name, name).x);
                // Row(): cost badge, a gap, the name, then the count column. The header: the deck's name, a gap, the card count.
                float needed = Mathf.Max(pad * 2 + row * .86f + line * .35f + nameWidth + row * 1.2f,
                    pad * 2 + Measure(deckName, header).x + line * .6f + Measure("30 cards", header).x);
                drawnWidth = Mathf.Clamp(needed, height * RowShare * WidthRows, height * RowShare * MaxWidthRows);
                title = Fit(deckName, drawnWidth - pad * 2 - line * .6f - Measure(remaining, header).x, header);
                measuredView = view; measuredRow = styleRow;
            }
            float column = line * ColumnRows, listWidth = drawnWidth - pad * 2;
            float panelWidth = drawnWidth + (columns ? column * ColumnNames.Length : 0f);
            float x = width - height * MarginShare - panelWidth;
            float panelHeight = pad * 2 + line * (1.3f + extra) + lines * row;
            GUI.DrawTexture(new Rect(x, top, panelWidth, panelHeight), panel);
            float y = top + pad, inner = panelWidth - pad * 2;
            Text(new Rect(x + pad, y, inner, line * 1.3f), title, header, Align.Left);
            Text(new Rect(x + pad, y, inner, line * 1.3f), remaining, header, Align.Right);
            y += line * 1.3f;
            if (statsShown)
            {
                Text(new Rect(x + pad, y, inner, line * StatsLine), recordLine ?? "", small, Align.Left);
                y += line * StatsLine;
            }
            float columnsX = x + pad + listWidth;
            if (columns)
            {
                for (int i = 0; i < ColumnNames.Length; i++)
                    Text(new Rect(columnsX + column * i, y, column, line * StatsLine), ColumnNames[i], small, Align.Center);
                y += line * StatsLine;
            }
            foreach (var entry in view.Rows)
            {
                Row(x + pad, y, listWidth, row, entry.Id, entry.Cost.ToString(), entry.Name, entry.Count, entry.Legendary);
                if (columns) Rates(columnsX, y, column, row, stats.For(entry.Id), entry.Count > 0);
                y += row;
            }
            if (view.Unknown > 0) { Row(x + pad, y, listWidth, row, null, "?", "Unknown card", view.Unknown, false); y += row; }
            if (view.UnseenRemoved > 0)
            {
                Text(new Rect(x + pad, y, inner, line), view.UnseenRemoved + (view.UnseenRemoved == 1 ? " card" : " cards") + " left unseen", note, Align.Left);
                y += line;
            }
            if (columns)
                Text(new Rect(x + pad, y, inner, line * StatsLine), stats.TrackedGames == 0 ? "Win rates start with your next game"
                    : "Win rates over " + stats.TrackedGames + (stats.TrackedGames == 1 ? " game" : " games") + " (" + settings.StatsKey + " hides them)", small, Align.Left);
        }
        catch (Exception error) { Fail(error); }
    }

    // The opponent tracker's panel, down the left edge: the opponent's name, their hand and deck counts, the cards seen from their
    // deck, then the ones they created. Sized like the deck tracker's, shrinking its rows to fit.
    private static OpponentTrackerView measuredOpponent;
    private static int measuredOpponentRow;
    private static float opponentWidth;
    private static void DrawOpponent()
    {
        var seen = opponent;
        if (seen == null) return;
        int height = Screen.height;
        bool created = seen.Created.Count > 0;
        int lines = Math.Max(1, seen.Deck.Count) + seen.Created.Count;
        float extra = StatsLine + (created ? StatsLine : 0f);
        float top = height * TopShare, available = height * BottomShare - top;
        float line = height * TextShare;
        float row = Mathf.Min(height * RowShare, (available - line * (1.6f + extra)) / lines);
        line = Mathf.Min(line, row);
        if (row < height * TextShare * .75f) { row = available / (lines + 1.6f + extra); line = row; }
        Styles(Mathf.Max(8, Mathf.RoundToInt(line)));
        float pad = line * .35f;
        string counts = (string.IsNullOrEmpty(opponentClass) ? "" : opponentClass + "  ·  ") + "Hand " + seen.Hand + "  ·  Deck " + seen.Remaining;
        if (!ReferenceEquals(measuredOpponent, seen) || measuredOpponentRow != styleRow)
        {
            float names = Measure("No cards seen yet", note).x;
            foreach (var entry in seen.Deck.Concat(seen.Created)) names = Mathf.Max(names, Measure(entry.Name, name).x);
            float needed = Mathf.Max(pad * 2 + row * .86f + line * .35f + names + row * 1.2f, pad * 2 + Measure(counts, small).x);
            opponentWidth = Mathf.Clamp(needed, height * RowShare * WidthRows, height * RowShare * MaxWidthRows);
            opponentTitle = Fit(opponentName, opponentWidth - pad * 2, header);
            measuredOpponent = seen; measuredOpponentRow = styleRow;
        }
        float x = height * MarginShare, listWidth = opponentWidth - pad * 2;
        float panelHeight = pad * 2 + line * (1.3f + extra) + lines * row;
        GUI.DrawTexture(new Rect(x, top, opponentWidth, panelHeight), panel);
        float y = top + pad;
        Text(new Rect(x + pad, y, listWidth, line * 1.3f), opponentTitle, header, Align.Left);
        y += line * 1.3f;
        Text(new Rect(x + pad, y, listWidth, line * StatsLine), counts, small, Align.Left);
        y += line * StatsLine;
        if (seen.Deck.Count == 0) { Text(new Rect(x + pad, y, listWidth, row), "No cards seen yet", note, Align.Left); y += row; }
        foreach (var entry in seen.Deck) { Row(x + pad, y, listWidth, row, entry.Id, entry.Cost.ToString(), entry.Name, entry.Count, entry.Legendary); y += row; }
        if (!created) return;
        Text(new Rect(x + pad, y, listWidth, line * StatsLine), "Created", small, Align.Left);
        y += line * StatsLine;
        foreach (var entry in seen.Created) { Row(x + pad, y, listWidth, row, entry.Id, entry.Cost.ToString(), entry.Name, entry.Count, entry.Legendary); y += row; }
    }

    // A card's three win rates (opening hand, drawn, played), each shaded red to green around 50%.
    private static void Rates(float x, float y, float column, float row, DeckCardStat stat, bool live)
    {
        int[] wins = { stat?.OpeningWins ?? 0, stat?.DrawnWins ?? 0, stat?.PlayedWins ?? 0 };
        int[] games = { stat?.OpeningGames ?? 0, stat?.DrawnGames ?? 0, stat?.PlayedGames ?? 0 };
        for (int i = 0; i < wins.Length; i++)
        {
            float[] shade = DeckStatsText.Shade(wins[i], games[i]);
            number.normal.textColor = new Color(shade[0], shade[1], shade[2], live ? 1f : .5f);
            Text(new Rect(x + column * i, y, column, row), DeckStatsText.Rate(wins[i], games[i]), number, Align.Center);
        }
        number.normal.textColor = Color.white;
    }

    // Under each card offered in the mulligan: how often it was kept when offered, and the win rate with it in the opening hand.
    private static void MulliganGuide()
    {
        var mulligan = MulliganManager.Get();
        if (mulligan == null || !mulligan.IsMulliganActive()) return;
        List<Card> cards = mulligan.GetStartingCards();
        if (cards == null) return;
        float line = Screen.height * TextShare;
        Styles(Mathf.Max(8, Mathf.RoundToInt(line)));
        foreach (Card card in cards)
        {
            var actor = card?.GetActor();
            if (actor == null || !actor.gameObject.activeInHierarchy) continue;
            Rect rect = MatchOverlay.GuiRect(actor.GetMeshRenderer(), actor.gameObject.layer);
            if (rect.width <= 0) continue;
            var stat = stats.For(card.GetEntity()?.GetCardId());
            var area = new Rect(rect.center.x - line * 3.3f, rect.yMax + line * .3f, line * 6.6f, line * 2.1f);
            GUI.DrawTexture(area, panel);
            string kept = stat == null || stat.OfferedGames == 0 ? "Kept: never offered" : "Kept " + stat.KeptGames + " of " + stat.OfferedGames;
            Text(new Rect(area.x, area.y + line * .1f, area.width, line), kept, small, Align.Center);
            int wins = stat?.OpeningWins ?? 0, games = stat?.OpeningGames ?? 0;
            float[] shade = DeckStatsText.Shade(wins, games);
            number.normal.textColor = new Color(shade[0], shade[1], shade[2]);
            Text(new Rect(area.x, area.y + line, area.width, line), "Mulligan win rate " + DeckStatsText.Rate(wins, games), number, Align.Center);
            number.normal.textColor = Color.white;
        }
    }

    // A badge left of the hero portrait, level with its middle: the attack total.
    private static void Counter(Rect hero, int attack)
    {
        if (attack < 0 || hero.width <= 0) return;
        float row = Screen.height * TextShare * 1.25f;
        Styles(Mathf.RoundToInt(Screen.height * TextShare));
        var area = new Rect(hero.xMin - row * 3.6f, hero.center.y - row * .6f, row * 3.2f, row * 1.2f);
        GUI.DrawTexture(area, attackPanel);
        Text(new Rect(area.x + row * .3f, area.y, area.width - row * .6f, area.height), "Attack", name, Align.Left);
        Text(new Rect(area.x + row * .3f, area.y, area.width - row * .6f, area.height), attack.ToString(), header, Align.Right);
    }

    // A card tile for another screen (NativeStatsScreen): the tracker's row at that height, with text sized to match.
    internal static void Tile(float x, float y, float width, float row, string id, string cost, string text, int count, bool legendary)
    {
        if (settings == null) return;
        Styles(Mathf.Max(8, Mathf.RoundToInt(row * TextShare / RowShare)));
        Row(x, y, width, row, id, cost, text, count, legendary);
    }

    // One card as a tile: a solid bar, the cost badge on the left, the card's art on the right fading out into the bar, the name over
    // both, and the copies left in a box at the right end (none for a single copy).
    private static void Row(float x, float y, float width, float row, string id, string cost, string text, int count, bool legendary)
    {
        float alpha = count > 0 ? 1f : .38f, gap = row * TileGap, top = y + gap / 2, tall = row - gap, size = tall * .82f, inset = (tall - size) / 2f;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(new Rect(x, top, width, tall), tile);
        float box = count != 1 ? tall : 0f;
        bool art = false;
        if (settings.CardArt && id != null && CardArt.TryGet(id, out var picture))
        {
            var area = new Rect(x + width * ArtStart, top, width * (1f - ArtStart) - box, tall);
            DrawPart(area, picture.Texture, CardArt.Fit(picture, area.width, area.height));
            GUI.DrawTexture(new Rect(area.x - 1f, area.y, area.width * FadeShare + 1f, area.height), fade);
            art = true;
        }
        GUI.DrawTexture(new Rect(x + inset, top + inset, size, size), count > 0 ? badge : zeroBadge);
        Text(new Rect(x + inset, top, size, tall), cost, number, Align.Center);
        Color nameColor = legendary ? new Color(1f, .74f, .3f) : new Color(.95f, .95f, .97f);
        name.normal.textColor = nameColor;
        var nameArea = new Rect(x + inset + size + tall * .3f, top, width - inset - size - tall * .3f - box - tall * .1f, tall);
        if (art) Shadowed(nameArea, text, name, Align.Left, nameColor); else Text(nameArea, text, name, Align.Left);
        if (box > 0)
        {
            GUI.DrawTexture(new Rect(x + width - box, top, box, tall), countBox);
            number.normal.textColor = count == 0 ? new Color(.7f, .7f, .72f) : new Color(1f, .82f, .35f);
            Text(new Rect(x + width - box, top, box, tall), count.ToString(), number, Align.Center);
            number.normal.textColor = Color.white;
        }
        GUI.color = Color.white;
    }

    private static void Styles(int row)
    {
        if (name != null && styleRow == row) return;
        panel = panel ?? Solid(PanelColor);
        badge = badge ?? Solid(new Color(.16f, .36f, .72f, .95f));
        zeroBadge = zeroBadge ?? Solid(new Color(.3f, .3f, .34f, .95f));
        attackPanel = attackPanel ?? Solid(new Color(.45f, .12f, .08f, .85f));
        fade = fade ?? Fade(TileColor);
        tile = tile ?? Solid(TileColor);
        countBox = countBox ?? Solid(new Color(.03f, .035f, .05f, 1f));
        GUIStyle Make(float share, Color color) => MatchOverlay.Label(Mathf.RoundToInt(row * share), color);
        name = Make(.6f, Color.white);
        number = Make(.6f, Color.white);
        header = Make(.72f, new Color(1f, 1f, 1f, .95f));
        note = Make(.52f, new Color(1f, .8f, .45f));
        small = Make(.5f, new Color(.82f, .85f, .9f));
        styleRow = row;
    }

    // A left-to-right fade from the panel's colour to clear, laid over the left part of the card art.
    private static Texture2D Fade(Color color)
    {
        const int steps = 32;
        var texture = new Texture2D(steps, 1) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (int i = 0; i < steps; i++) texture.SetPixel(i, 0, new Color(color.r, color.g, color.b, 1f - (float)i / (steps - 1)));
        texture.Apply();
        return texture;
    }
}
