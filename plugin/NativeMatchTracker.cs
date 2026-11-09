using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using static MatchOverlay;

// The match tracker, two IMGUI overlays during a match. The deck tracker: one panel down the right edge, under the brand label,
// listing the cards still in the player's deck (DeckTrackerRows). It reads only what the player has seen: OwnDeckLedger's
// observations of the player's own side. The attack counters (off by default): a number beside each hero (AttackCounter),
// from the public board. Both refresh only when the native board has settled (ClientBridge.BoardSettled), so a draw never
// leaves the list, and an attack never changes a counter, before its animation shows it. Before the first refresh the deck
// tracker shows the chosen decklist. Neither takes input or changes game state; the toggle key (MatchOverlay) only hides
// them. Every path fails closed: an error disables both for the session with one warning.
internal static class NativeMatchTracker
{
    private static MatchTrackerSettings settings;
    private static ManualLogSource log;
    private static bool failed;
    private static NativeMatch tracked;
    private static int observedHistory = -1;
    private static string lastSummary;
    private static DeckTrackerView view;
    // The attack counters: friendly and opposing, and where each hero is on screen (GUI coordinates, empty when unknown).
    private static int friendlyAttack = -1, opposingAttack = -1;
    private static Rect friendlyHero, opposingHero;
    private static int heroesFrame = -100;
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
            if (!settings.DeckTracker && !settings.AttackCounter || game == null || !MatchOverlay.InMatch()) { Forget(); return; }
            if (!ReferenceEquals(match, tracked))
            {
                Forget(); tracked = match;
                deckName = string.IsNullOrWhiteSpace(match.PlayerDeckName) ? "Deck" : match.PlayerDeckName.Trim();
                view = DeckTrackerRows.Decklist(game.Player1.DeckCards.Select(c => c.Id).ToList(), Describe);
            }
#if ENGINE_SOURCE
            if (settings.AttackCounter && Time.frameCount - heroesFrame >= 30) { heroesFrame = Time.frameCount; PlaceHeroes(); }
            int history = game.PowerHistory?.Full?.Count ?? 0;
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
            observedHistory = history;
            string summary = view?.Summary ?? "off";
            summary += " attack=" + friendlyAttack + "/" + opposingAttack;
            if (summary != lastSummary) { lastSummary = summary; log.LogInfo("DECK_TRACKER " + summary); }
#endif
        }
        catch (Exception error) { Fail(error); }
    }

    private static void Forget()
    {
        tracked = null; view = null; observedHistory = -1; lastSummary = null; measuredView = null; drawnWidth = 0f; deckName = title = "Deck";
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
        log?.LogWarning("MATCH_TRACKER disabled for the session: " + error);
    }

    // Drawing. Sizes are shares of the screen height, like the brand label; rows shrink to fit a long list.
    private static GUIStyle name, number, header, note;
    private static int styleRow;
    private static Texture2D panel, badge, zeroBadge, attackPanel;
    internal static readonly Color PanelColor = new Color(.05f, .06f, .09f, .78f);
    internal const float RowShare = .026f, WidthRows = 8.5f, MaxWidthRows = 13f, TopShare = .075f, BottomShare = .9f, MarginShare = .012f;
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
            if (!settings.DeckTracker || view == null) return;
            int height = Screen.height, width = Screen.width;
            int lines = view.Rows.Count + (view.Unknown > 0 ? 1 : 0);
            float top = height * TopShare, available = height * BottomShare - top;
            float row = Mathf.Min(height * RowShare, available / (lines + 1.6f + (view.UnseenRemoved > 0 ? 1f : 0f)));
            Styles(Mathf.Max(8, Mathf.RoundToInt(row)));
            float pad = row * .35f;
            string remaining = view.Remaining + (view.Remaining == 1 ? " card" : " cards");
            if (!ReferenceEquals(measuredView, view) || measuredRow != styleRow)
            {
                nameWidth = view.Unknown > 0 ? Measure("Unknown card", name).x : 0f;
                foreach (var entry in view.Rows) nameWidth = Mathf.Max(nameWidth, Measure(entry.Name, name).x);
                // Row(): cost badge, a gap, the name, then the count column. The header: the deck's name, a gap, the card count.
                float needed = Mathf.Max(pad * 2 + row * .86f + row * .35f + nameWidth + row * 1.6f,
                    pad * 2 + Measure(deckName, header).x + row * .6f + Measure("30 cards", header).x);
                drawnWidth = Mathf.Clamp(needed, height * RowShare * WidthRows, height * RowShare * MaxWidthRows);
                title = Fit(deckName, drawnWidth - pad * 2 - row * .6f - Measure(remaining, header).x, header);
                measuredView = view; measuredRow = styleRow;
            }
            float panelWidth = drawnWidth;
            float x = width - height * MarginShare - panelWidth;
            float panelHeight = pad * 2 + row * 1.3f + lines * row + (view.UnseenRemoved > 0 ? row : 0f);
            GUI.DrawTexture(new Rect(x, top, panelWidth, panelHeight), panel);
            float y = top + pad, inner = panelWidth - pad * 2;
            Text(new Rect(x + pad, y, inner, row * 1.3f), title, header, Align.Left);
            Text(new Rect(x + pad, y, inner, row * 1.3f), remaining, header, Align.Right);
            y += row * 1.3f;
            foreach (var entry in view.Rows) { Row(x + pad, y, inner, row, entry.Cost.ToString(), entry.Name, entry.Count, entry.Legendary); y += row; }
            if (view.Unknown > 0) { Row(x + pad, y, inner, row, "?", "Unknown card", view.Unknown, false); y += row; }
            if (view.UnseenRemoved > 0)
                Text(new Rect(x + pad, y, inner, row), view.UnseenRemoved + (view.UnseenRemoved == 1 ? " card" : " cards") + " left unseen", note, Align.Left);
        }
        catch (Exception error) { Fail(error); }
    }

    // A badge left of the hero portrait, level with its middle: the attack total.
    private static void Counter(Rect hero, int attack)
    {
        if (attack < 0 || hero.width <= 0) return;
        float row = Screen.height * RowShare * 1.25f;
        Styles(Mathf.RoundToInt(Screen.height * RowShare));
        var area = new Rect(hero.xMin - row * 3.6f, hero.center.y - row * .6f, row * 3.2f, row * 1.2f);
        GUI.DrawTexture(area, attackPanel);
        Text(new Rect(area.x + row * .3f, area.y, area.width - row * .6f, area.height), "Attack", name, Align.Left);
        Text(new Rect(area.x + row * .3f, area.y, area.width - row * .6f, area.height), attack.ToString(), header, Align.Right);
    }

    private static void Row(float x, float y, float width, float row, string cost, string text, int count, bool legendary)
    {
        float alpha = count > 0 ? 1f : .38f, size = row * .86f, inset = (row - size) / 2f;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(new Rect(x, y + inset, size, size), count > 0 ? badge : zeroBadge);
        Text(new Rect(x, y, size, row), cost, number, Align.Center);
        name.normal.textColor = legendary ? new Color(1f, .74f, .3f) : new Color(.93f, .93f, .95f);
        Text(new Rect(x + size + row * .35f, y, width - size - row * 1.6f, row), text, name, Align.Left);
        if (count != 1) Text(new Rect(x, y, width, row), count == 0 ? "0" : "×" + count, number, Align.Right);
        GUI.color = Color.white;
    }

    private static void Styles(int row)
    {
        if (name != null && styleRow == row) return;
        panel = panel ?? Solid(PanelColor);
        badge = badge ?? Solid(new Color(.16f, .36f, .72f, .95f));
        zeroBadge = zeroBadge ?? Solid(new Color(.3f, .3f, .34f, .95f));
        attackPanel = attackPanel ?? Solid(new Color(.45f, .12f, .08f, .85f));
        GUIStyle Make(float share, Color color) => MatchOverlay.Label(Mathf.RoundToInt(row * share), color);
        name = Make(.6f, Color.white);
        number = Make(.6f, Color.white);
        header = Make(.72f, new Color(1f, 1f, 1f, .95f));
        note = Make(.52f, new Color(1f, .8f, .45f));
        styleRow = row;
    }
}
