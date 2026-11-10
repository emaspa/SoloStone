using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static MatchOverlay;

// The history behind the hovered card (CardHistory): while the player hovers a card that resurrects minions that died, recasts
// spells or repeats Battlecries, the cards the game has recorded for it so far show beside it as the game's own card models at 40%
// of the enlarged card (built like BigCard's extra card: DefLoader.GetFullDef, the hand actor prefab, Actor.SetFullDef), with a
// title and a count over each card that came more than once. A hand card shows them to its right (left when there is no room), a
// card in play to its left, clear of the game's enlarged card. Only cards the player can read: their own hand and anything in play;
// never a card in the opponent's hand or a deck. Its own switch (MatchTrackerSettings CardHistory). It fails closed on its
// own: an error turns only this off for the session, and the models go whenever nothing draws them (Sweep).
internal static class NativeCardHistory
{
    private static bool failed;
    private static int cachedEntity = -1, cachedHistory = -1;
    private static string cachedTitle;
    private static List<KeyValuePair<RecordedCard, int>> cachedLines;
    private static GUIStyle title, line;
    private static int styleSize;
    private static BepInEx.Logging.ManualLogSource log;

    internal static void Initialize(BepInEx.Logging.ManualLogSource logger) => log = logger;
    internal static void Forget() { cachedEntity = cachedHistory = -1; cachedLines = null; cachedTitle = null; Clear(); }

    // At most this many models; the title says how many there are in all.
    private const int MaxThumbnails = 15;
    private const float ThumbnailShare = .4f;
    private static readonly List<GameObject> thumbnails = new List<GameObject>();
    private static readonly List<int> thumbnailCounts = new List<int>();
    private static string thumbnailKey;
    private static int drawnFrame = -10;
    private static float thumbnailScale;
    private static System.Reflection.MethodInfo instantiate;

    // Called every frame from NativeMatchTracker.Tick: the models go when nothing drew them (hover ended, overlay hidden or off).
    internal static void Sweep()
    {
        if (thumbnails.Count > 0 && Time.frameCount - drawnFrame > 2) Clear();
    }

    private static void Clear()
    {
        foreach (var thumbnail in thumbnails) if (thumbnail != null) UnityEngine.Object.Destroy(thumbnail);
        thumbnails.Clear(); thumbnailCounts.Clear(); thumbnailKey = null;
    }

    internal static void Draw()
    {
        if (failed) return;
        try
        {
            var card = InputManager.Get()?.GetMousedOverCard();
            if (card == null) return;
            int id = card.GetEntity()?.GetEntityId() ?? 0;
            if (id == 0) return;
#if ENGINE_SOURCE
            var game = ClientBridge.CurrentMatch?.Game;
            if (game == null) return;
            int history = game.PowerHistory?.Full?.Count ?? 0;
            if (id != cachedEntity || history != cachedHistory)
            {
                cachedEntity = id; cachedHistory = history;
                Lookup(game, id);
                if (cachedLines != null) log?.LogInfo("CARD_HISTORY " + id + " " + cachedTitle);
            }
#endif
            if (cachedLines == null) return;
            var rect = NativeCardChips.CardRect(card);
            if (rect.width <= 0) return;
            bool inHand = card.GetZone() is ZoneHand;
            var actor = card.GetActor();
            if (actor == null) return;
            Show(id + ":" + cachedHistory, actor, rect, inHand);
        }
        catch (Exception error)
        {
            failed = true;
            try { Clear(); } catch (Exception) { }
            log?.LogWarning("CARD_HISTORY off for the session: " + error);
        }
    }

#if ENGINE_SOURCE
    private static void Lookup(SabberStoneCore.Model.Game game, int id)
    {
        cachedLines = null; cachedTitle = null;
        if (!game.IdEntityDic.TryGetValue(id, out var entity) || !(entity is SabberStoneCore.Model.Entities.IPlayable playable)) return;
        var zone = playable.Zone?.Type ?? SabberStoneCore.Enums.Zone.INVALID;
        // The opponent's hand and both decks are hidden from the player.
        if (zone == SabberStoneCore.Enums.Zone.DECK || zone == SabberStoneCore.Enums.Zone.INVALID
            || zone == SabberStoneCore.Enums.Zone.HAND && playable.Controller != game.Player1) return;
        var query = HistoryQuery.For(LocalCollectionUI.RulesText(playable.Card));
        if (query == null) return;
        var owner = playable.Controller;
        var whose = query.Enemy ? owner.Opponent : owner;
        IEnumerable<RecordedCard> cards;
        if (query.Kind == HistoryKind.Died)
            cards = whose.GraveyardZone
                .Where(e => e is SabberStoneCore.Model.Entities.Minion && e[SabberStoneCore.Enums.GameTag.TO_BE_DESTROYED] == 1)
                .Select(e => Describe(e.Card));
        else
            cards = whose.PlayHistory
                .Where(p => p.SourceCard != null && (query.Kind == HistoryKind.Spells ? p.SourceCard.Type == SabberStoneCore.Enums.CardType.SPELL
                    : p.SourceCard.Type == SabberStoneCore.Enums.CardType.MINION && p.SourceCard[SabberStoneCore.Enums.GameTag.BATTLECRY] == 1))
                .Select(p => Describe(p.SourceCard));
        var all = cards.ToList();
        cachedLines = query.Entries(all);
        cachedTitle = query.Title(all.Count(query.Keeps));
    }

    private static RecordedCard Describe(SabberStoneCore.Model.Card card) => new RecordedCard {
        Id = card.Id, Name = card.Name, Cost = card.Cost, Legendary = card.Rarity == SabberStoneCore.Enums.Rarity.LEGENDARY, Races = card.Races.Select(r => r.ToString()).ToList(), Deathrattle = card[SabberStoneCore.Enums.GameTag.DEATHRATTLE] == 1 };
#endif

    // The models beside the hovered card, built once per card and history, placed every frame.
    private static void Show(string key, Actor hovered, Rect card, bool inHand)
    {
        drawnFrame = Time.frameCount;
        int size = Mathf.Max(8, Mathf.RoundToInt(Screen.height * NativeMatchTracker.TextShare));
        Styles(size);
        int layer = hovered.gameObject.layer;
        var view = CameraUtils.FindFirstByLayerMask(1 << layer);
        if (view == null) return;
        if (key != thumbnailKey)
        {
            Clear();
            thumbnailKey = key;
            foreach (var entry in cachedLines.Take(MaxThumbnails))
            {
                var model = Spawn(entry.Key.Id, layer);
                if (model == null) continue;
                thumbnails.Add(model); thumbnailCounts.Add(entry.Value);
            }
            thumbnailScale = 0f;
        }
        // The enlarged card: the hovered hand card itself, or (for a card in play) the game's big card, about this share of the screen.
        float bigHeight = inHand ? card.height : Screen.height * .42f;
        float height = bigHeight * ThumbnailShare, width = height * .72f, gap = height * .06f, heading = size * 1.6f;
        int count = Mathf.Max(1, thumbnails.Count);
        float room = inHand ? Screen.width - card.xMax : card.xMin - (inHand ? 0f : bigHeight * .75f);
        int perRow = Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(room, Screen.width - card.xMax, card.xMin) - gap) / (width + gap)), 1, 5);
        perRow = Mathf.Min(perRow, count);
        int rows = Mathf.CeilToInt(count / (float)perRow);
        float blockWidth = perRow * (width + gap) + gap, blockHeight = heading + rows * (height + gap);
        float x = inHand ? card.xMax + gap : card.xMin - bigHeight * .75f - blockWidth;
        if (inHand && x + blockWidth > Screen.width) x = card.xMin - blockWidth;
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - blockWidth));
        float y = Mathf.Clamp(card.yMin, 0f, Mathf.Max(0f, Screen.height - blockHeight));
        Shadowed(new Rect(x + gap, y, blockWidth, heading), cachedTitle + (cachedLines.Count > thumbnails.Count && thumbnails.Count > 0
            ? ", first " + thumbnails.Count : ""), title, Align.Left, new Color(1f, .85f, .5f));
        if (cachedLines.Count == 0) { Shadowed(new Rect(x + gap, y + heading, blockWidth, heading), "None yet.", line, Align.Left, Color.white); return; }
        float depth = view.WorldToScreenPoint(hovered.transform.position).z;
        for (int i = 0; i < thumbnails.Count; i++)
        {
            var model = thumbnails[i];
            if (model == null) continue;
            int row = i / perRow, column = i % perRow;
            var centre = new Vector2(x + gap + column * (width + gap) + width / 2f, y + heading + row * (height + gap) + height / 2f);
            Place(model, hovered, view, centre, depth * .98f, height);
            if (thumbnailCounts[i] > 1)
                NativeCardChips.Badge("x" + thumbnailCounts[i], Color.white, centre.x + width * .5f, centre.y + height * .45f, NativeCardChips.Anchor.Right);
        }
    }

    // One model centred on a GUI point at the given depth, turned like the hovered card, scaled so it is that tall on screen.
    private static void Place(GameObject model, Actor hovered, Camera view, Vector2 centre, float depth, float height)
    {
        model.transform.rotation = hovered.transform.rotation;
        model.transform.position = view.ScreenToWorldPoint(new Vector3(centre.x, Screen.height - centre.y, depth));
        if (thumbnailScale <= 0f)
        {
            // Measured once on the first model: the prefab's own scale to the wanted height on screen.
            var actor = model.GetComponent<Actor>();
            Rect drawn = MatchOverlay.GuiRect(actor?.GetMeshRenderer(), model.layer);
            if (drawn.height <= 0f) return;
            thumbnailScale = model.transform.localScale.x * height / drawn.height;
        }
        model.transform.localScale = Vector3.one * thumbnailScale;
    }

    // A card model as the game builds its big card's extra card: the full definition, the hand actor, no collider (so the mouse still
    // finds the real cards), drawn on the hovered card's layer.
    private static GameObject Spawn(string cardId, int layer)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        var loader = NativeUIProbe.RewardAssetLoader;
        if (loader == null) return null;
        instantiate = instantiate ?? loader.GetType().GetMethods().First(m => m.Name == "InstantiatePrefab" && m.ReturnType == typeof(GameObject)
            && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name == "AssetReference" && m.GetParameters()[1].ParameterType.IsEnum);
        using (var full = DefLoader.Get()?.GetFullDef(cardId))
        {
            if (full?.EntityDef == null) return null;
            string path = ActorNames.GetHandActor(full.EntityDef, TAG_PREMIUM.NORMAL);
            var parameters = instantiate.GetParameters();
            var reference = parameters[0].ParameterType.GetMethod("op_Implicit", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null).Invoke(null, new object[] { path });
            var model = instantiate.Invoke(loader, new[] { reference, Enum.ToObject(parameters[1].ParameterType, 2) }) as GameObject;
            var actor = model?.GetComponent<Actor>();
            if (actor == null) { if (model != null) UnityEngine.Object.Destroy(model); return null; }
            actor.SetPremium(TAG_PREMIUM.NORMAL);
            actor.SetFullDef(full);
            actor.UpdateAllComponents();
            foreach (var part in model.GetComponentsInChildren<Transform>(true)) part.gameObject.layer = layer;
            foreach (var collider in model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            model.transform.position = new Vector3(1000f, -1000f, -1000f);
            return model;
        }
    }

    private static void Styles(int size)
    {
        if (title != null && styleSize == size) return;
        title = MatchOverlay.Label(size, new Color(1f, .85f, .5f));
        line = MatchOverlay.Label(size, Color.white);
        styleSize = size;
    }
}
