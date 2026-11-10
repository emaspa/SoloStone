using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static MatchOverlay;

// The card chips (CardChips): under each card in the opponent's hand, on each hero power and on each quest or other counter beside a
// hero. NativeMatchTracker refreshes them when the native board has settled and draws them each frame. The facts come from the engine
// as the player's fair view of the history shows them (HistoryFacts.ProjectForFairView): a creator or a returned card is named only
// when the player saw it, an opponent's Secret shows nothing, and a hidden card's identity never does. Entity ids are public in the
// client too, and the client's cards carry the engine's ids. It fails closed on its own: an error turns only the chips off for the
// session.
internal static class NativeCardChips
{
    private static Dictionary<int, Chip> chips = new Dictionary<int, Chip>();
    private static bool failed;
    private static string lastSummary;
    private static GUIStyle label, detail;
    private static int styleSize;
    private static float pillHeight;
    private static Texture2D pill, detailBack, detailEdge;
    private static readonly Color[] TagColors = {
        new Color(1f, .78f, .35f),   // Created
        new Color(.55f, .85f, 1f),   // Drawn
        new Color(.6f, 1f, .6f),     // Returned
        new Color(1f, .5f, .45f),    // Temporary
    };

    internal static bool Failed => failed;
    private static BepInEx.Logging.ManualLogSource log;
    internal static void Initialize(BepInEx.Logging.ManualLogSource logger) => log = logger;

    internal static void Forget() { chips = new Dictionary<int, Chip>(); lastSummary = null; }

#if ENGINE_SOURCE
    internal static void Observe(SabberStoneCore.Model.Game game, NativeMatch match, bool hand, bool counters)
    {
        if (failed) return;
        try
        {
            var found = new Dictionary<int, Chip>();
            if (hand) HandChips(game, match, found);
            if (counters) foreach (var player in new[] { game.Player1, game.Player2 })
            {
                var power = player.Hero?.HeroPower;
                var imbued = CardChips.HeroPower(SabberStoneCore.Actions.ImbueRules.Count(player));
                if (power != null && imbued != null) found[power.Id] = imbued;
                foreach (var counter in player.SecretZone)
                {
                    // An opponent's Secret is hidden until it is revealed; their quests, sidequests and other counters are public.
                    if (player != game.Player1 && counter.Card.IsSecret && counter[SabberStoneCore.Enums.GameTag.REVEALED] == 0) continue;
                    var quest = CardChips.Quest(counter.Card.Name, counter[SabberStoneCore.Enums.GameTag.QUEST_PROGRESS],
                        counter[SabberStoneCore.Enums.GameTag.QUEST_PROGRESS_TOTAL]);
                    if (quest != null) found[counter.Id] = quest;
                }
            }
            chips = found;
            string summary = CardChips.Summary(found);
            if (summary != lastSummary) { lastSummary = summary; log?.LogInfo("CARD_CHIPS " + summary); }
        }
        catch (Exception error)
        {
            failed = true; chips = new Dictionary<int, Chip>();
            log?.LogWarning("CARD_CHIPS off for the session: " + error);
        }
    }

    // The opponent's hand. The full history says when and how each card came (its entry, matched by sequence); the fair view says
    // which source the player saw. Nothing shows before the mulligan is over: until then no card is settled as kept or replaced.
    private static void HandChips(SabberStoneCore.Model.Game game, NativeMatch match, Dictionary<int, Chip> found)
    {
        var facts = game.HistoryFacts;
        var firstTurn = facts.Query.Events<SabberStoneCore.Model.TurnStartedFact>().FirstOrDefault();
        if (firstTurn == null) return;
        var them = game.Player2;
        var fair = facts.ProjectForFairView(game, game.Player1.PlayerId);
        var fairEntries = new Dictionary<long, SabberStoneCore.Model.HandEnteredFact>();
        foreach (var entry in fair.Query.HandEntries()) fairEntries[entry.Sequence] = entry;
        var entries = new Dictionary<int, SabberStoneCore.Model.HandEnteredFact>();
        foreach (var entry in facts.Query.HandEntries()) if (entry.ControllerId == them.Id) entries[entry.EntityId] = entry;
        // What the player saw each card as, by entity and sequence: the identity a returned card had before it went back to hand.
        var full = new Dictionary<long, SabberStoneCore.Model.HistoryCardFact>();
        foreach (var fact in facts.Query.Events<SabberStoneCore.Model.HistoryCardFact>()) full[fact.Sequence] = fact;
        var seen = new List<KeyValuePair<long, KeyValuePair<int, string>>>();
        foreach (var fact in fair.Query.Events<SabberStoneCore.Model.HistoryCardFact>())
            if (fact.DefinitionKnown && full.TryGetValue(fact.Sequence, out var source))
                seen.Add(new KeyValuePair<long, KeyValuePair<int, string>>(fact.Sequence, new KeyValuePair<int, string>(source.EntityId, SabberStoneCore.Model.Cards.FromAssetId(fact.DbfId)?.Name)));

        foreach (var card in them.HandZone)
        {
            entries.TryGetValue(card.Id, out var entry);
            var hand = new HandFacts { Temporary = card[SabberStoneCore.Enums.GameTag.GHOSTLY] == 1 };
            if (entry == null || entry.Sequence < firstTurn.Sequence)
            {
                hand.Turn = 0;
                if (card.Card.Id == "GAME_005") hand.Origin = HandOrigin.Coin;
                else if (entry != null && entry.EntryKind == SabberStoneCore.Model.HistoryHandEntryKind.Generated) hand.Origin = HandOrigin.Created;
                else hand.Origin = match.OpponentDealt(card.Id) ? HandOrigin.Kept : HandOrigin.Mulligan;
            }
            else
            {
                hand.Turn = entry.Turn;
                fairEntries.TryGetValue(entry.Sequence, out var fairEntry);
                hand.Source = Name(game, fairEntry?.SourceId ?? 0, card.Id);
                var from = entry.FromZone;
                if (from == SabberStoneCore.Enums.Zone.PLAY || from == SabberStoneCore.Enums.Zone.GRAVEYARD)
                {
                    hand.Origin = HandOrigin.Returned;
                    hand.Known = seen.LastOrDefault(s => s.Key < entry.Sequence && s.Value.Key == card.Id).Value.Value;
                }
                else if (from == SabberStoneCore.Enums.Zone.DECK || entry.EntryKind == SabberStoneCore.Model.HistoryHandEntryKind.Draw
                    || entry.EntryKind == SabberStoneCore.Model.HistoryHandEntryKind.TurnStartDraw)
                {
                    hand.Origin = HandOrigin.Drawn;
                    // The draw at the start of a turn has no card behind it.
                    if (entry.EntryKind == SabberStoneCore.Model.HistoryHandEntryKind.TurnStartDraw) hand.Source = null;
                }
                else if (entry.EntryKind == SabberStoneCore.Model.HistoryHandEntryKind.Generated) hand.Origin = HandOrigin.Created;
                else hand.Origin = HandOrigin.Other;
            }
            var chip = CardChips.Hand(hand);
            if (chip != null) found[card.Id] = chip;
        }
    }

    // A source's name when the fair view kept it (it is not hidden) and it is a card: not a player or the game, not the card itself.
    private static string Name(SabberStoneCore.Model.Game game, int id, int self)
    {
        if (id == 0 || id == self || !game.IdEntityDic.TryGetValue(id, out var entity)) return null;
        return entity is SabberStoneCore.Model.Entities.IPlayable playable ? playable.Card?.Name : null;
    }
#endif

    // Each frame: the chips over the client's cards, where they are now. Each chip is a pill with its label. The hand's chips sit on
    // one line under the fan, one per card, and shrink their text to fit between two cards.
    internal static void Draw()
    {
        if (failed || chips.Count == 0) return;
        try
        {
            var state = GameState.Get();
            if (state == null) return;
            int size = Size;
            Styles(size);
            Chip hovered = null; Rect hoveredArea = Rect.zero;
            var opposing = state.GetOpposingSidePlayer();
            var hand = opposing?.GetHandZone()?.GetCards();
            if (hand != null)
            {
                var placed = new List<KeyValuePair<Rect, Chip>>();
                foreach (Card card in hand)
                {
                    var rect = CardRect(card);
                    if (rect.width <= 0 || !chips.TryGetValue(card.GetEntity()?.GetEntityId() ?? 0, out var chip)) continue;
                    placed.Add(new KeyValuePair<Rect, Chip>(rect, chip));
                }
                if (placed.Count > 0)
                {
                    // A card being played is drawn larger than the rest of the hand and leaves it: no chip over it.
                    var heights = placed.Select(p => p.Key.height).OrderBy(h => h).ToList();
                    float usual = heights[heights.Count / 2];
                    placed.RemoveAll(p => p.Key.height > usual * 1.2f);
                    float step = float.MaxValue, bottom = 0f;
                    var centres = placed.Select(p => p.Key.center.x).OrderBy(x => x).ToList();
                    for (int i = 1; i < centres.Count; i++) step = Mathf.Min(step, centres[i] - centres[i - 1]);
                    foreach (var pair in placed) bottom = Mathf.Max(bottom, pair.Key.yMax);
                    float room = Mathf.Min(step - Mathf.Max(3f, size * .35f), size * 6f);
                    foreach (var pair in placed)
                    {
                        var area = Pill(pair.Value, pair.Key.center.x, bottom - pillHeight * .45f, room);
                        if (area.Contains(Event.current.mousePosition)) { hovered = pair.Value; hoveredArea = area; }
                    }
                }
            }
            foreach (var player in new[] { state.GetFriendlySidePlayer(), opposing })
            {
                if (player == null) continue;
                var power = player.GetHeroPowerCard();
                if (power != null && chips.TryGetValue(power.GetEntity()?.GetEntityId() ?? 0, out var imbued))
                {
                    var rect = CardRect(power);
                    if (rect.width > 0)
                    {
                        var area = Pill(imbued, rect.center.x, rect.yMin - pillHeight / 2f, size * 6f);
                        if (area.Contains(Event.current.mousePosition)) { hovered = imbued; hoveredArea = area; }
                    }
                }
                var counters = player.GetSecretZone()?.GetCards();
                if (counters == null) continue;
                foreach (Card counter in counters)
                {
                    if (!chips.TryGetValue(counter.GetEntity()?.GetEntityId() ?? 0, out var quest)) continue;
                    var rect = CardRect(counter);
                    if (rect.width <= 0) continue;
                    // To the left of the quest marker, level with its middle.
                    var area = Pill(quest, rect.xMin - size * .3f, rect.center.y - pillHeight / 2f, size * 6f, Anchor.Right);
                    if (area.Contains(Event.current.mousePosition)) { hovered = quest; hoveredArea = area; }
                }
            }
            if (hovered != null) Detail(hovered, hoveredArea);
        }
        catch (Exception error)
        {
            failed = true; chips = new Dictionary<int, Chip>();
            log?.LogWarning("CARD_CHIPS off for the session: " + error);
        }
    }

    // A card's rendered bounds in GUI coordinates: its main mesh, else all of its renderers.
    internal static Rect CardRect(Card card)
    {
        var actor = card?.GetActor();
        if (actor == null || !actor.gameObject.activeInHierarchy) return Rect.zero;
        Rect rect = GuiRect(actor.GetMeshRenderer(), actor.gameObject.layer);
        if (rect.width > 0) return rect;
        Camera view = CameraUtils.FindFirstByLayerMask(1 << actor.gameObject.layer);
        if (view == null) return Rect.zero;
        Rect screen = DeckLayout.ScreenRect(actor.gameObject, view);
        return screen.width <= 0 ? Rect.zero : new Rect(screen.xMin, Screen.height - screen.yMax, screen.width, screen.height);
    }

    internal static float PillHeight { get { Styles(Size); return pillHeight; } }

    private static int Size => Mathf.Max(8, Mathf.RoundToInt(Screen.height * NativeMatchTracker.TextShare * .54f));

    internal enum Anchor { Centre, Left, Right }

    // A pill in the chips' style for another overlay (the attack counters, the turn countdown): the text, its colour, and where x is
    // (its centre, its left edge or its right edge), centred on y. sizeAs, when given, sets the width (the widest reading of a
    // changing number, so the pill does not twitch). Returns where it went.
    internal static Rect Badge(string text, Color color, float x, float y, Anchor anchor, string sizeAs = null)
    {
        Styles(Size);
        var chip = new Chip { Label = text };
        float width = Mathf.Max(Measure(text, label).x, sizeAs == null ? 0f : Measure(sizeAs, label).x) + pillHeight * .68f;
        return Pill(chip, x, y - pillHeight / 2f, Screen.width, anchor, color, width);
    }

    // One pill with its top at y, at most room wide: the label shrinks (to two thirds) before the pill outgrows it. x is its centre,
    // its left edge or its right edge (anchor). The label's colour says how the card came (ChipTag), red when it is Temporary.
    private static Rect Pill(Chip chip, float x, float y, float room, Anchor anchor = Anchor.Centre, Color? color = null, float least = 0f)
    {
        float h = pillHeight, pad = h * .34f;
        int font = styleSize;
        Vector2 text = Measure(chip.Label, label);
        while (font > styleSize * 2 / 3 && text.x + pad * 2 > room)
        {
            label.fontSize = --font;
            text = Measure(chip.Label, label);
        }
        float width = Mathf.Max(least, Mathf.Min(Mathf.Max(h * 1.4f, text.x + pad * 2), Mathf.Max(h * 1.4f, room)));
        float left = anchor == Anchor.Left ? x : anchor == Anchor.Right ? x - width : x - width / 2f;
        var area = new Rect(Mathf.Round(left), Mathf.Round(y), Mathf.Round(width), h);
        float half = h / 2f;
        DrawPart(new Rect(area.x, area.y, half, h), pill, new Rect(0f, 0f, .5f, 1f));
        DrawPart(new Rect(area.x + half, area.y, area.width - h, h), pill, new Rect(.5f, 0f, 1f / pill.width, 1f));
        DrawPart(new Rect(area.xMax - half, area.y, half, h), pill, new Rect(.5f, 0f, .5f, 1f));
        label.normal.textColor = color.HasValue ? color.Value : chip.Tags.Contains(ChipTag.Temporary) ? TagColors[(int)ChipTag.Temporary]
            : chip.Tags.Count > 0 ? TagColors[(int)chip.Tags[0]] : Color.white;
        Text(area, chip.Label, label, Align.Center);
        label.fontSize = styleSize;
        return area;
    }

    // The hovered chip's detail, under it (over it near the bottom of the screen), kept on screen.
    private static void Detail(Chip chip, Rect area)
    {
        if (string.IsNullOrEmpty(chip.Detail)) return;
        string[] lines = chip.Detail.Split('\n');
        float width = 0f, line = styleSize * 1.35f, pad = styleSize * .5f;
        foreach (string text in lines) width = Mathf.Max(width, Measure(text, detail).x);
        var box = new Rect(area.center.x - width / 2f - pad, area.yMax + pad * .5f, width + pad * 2, line * lines.Length + pad * 2);
        if (box.yMax > Screen.height) box.y = area.yMin - box.height - pad * .5f;
        box.x = Mathf.Clamp(box.x, 0f, Mathf.Max(0f, Screen.width - box.width));
        GUI.DrawTexture(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), detailEdge);
        GUI.DrawTexture(box, detailBack);
        for (int i = 0; i < lines.Length; i++) Text(new Rect(box.x + pad, box.y + pad + line * i, width, line), lines[i], detail, Align.Left);
    }

    private static void Styles(int size)
    {
        if (label != null && styleSize == size) return;
        detailBack = detailBack ?? Solid(new Color(.04f, .05f, .07f, .95f));
        detailEdge = detailEdge ?? Solid(new Color(.75f, .62f, .35f, .9f));
        pillHeight = Mathf.Round(size * 1.38f);
        int across = Mathf.Max(8, Mathf.RoundToInt(pillHeight));
        if (pill != null) UnityEngine.Object.Destroy(pill);
        pill = Disc(across, new Color(.06f, .07f, .1f, .92f), new Color(.78f, .64f, .36f, .95f), Mathf.Max(1f, across / 14f));
        label = MatchOverlay.Label(size, Color.white);
        detail = MatchOverlay.Label(size, new Color(.92f, .93f, .96f));
        styleSize = size;
    }

    // A smooth disc, across pixels wide: fill inside, an edge ring of the given width, transparent outside. The pill's two halves
    // are its two halves, and its middle is its centre column stretched.
    private static Texture2D Disc(int across, Color fill, Color ring, float edgeWidth)
    {
        var texture = new Texture2D(across, across, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        float radius = across / 2f;
        var pixels = new Color[across * across];
        for (int y = 0; y < across; y++)
            for (int x = 0; x < across; x++)
            {
                float dx = x + .5f - radius, dy = y + .5f - radius, d = Mathf.Sqrt(dx * dx + dy * dy);
                Color c = Color.Lerp(fill, ring, Mathf.Clamp01(d - (radius - edgeWidth) + .5f));
                c.a *= Mathf.Clamp01(radius - d + .5f);
                pixels[y * across + x] = c;
            }
        texture.SetPixels(pixels); texture.Apply();
        return texture;
    }
}
