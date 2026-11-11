using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static MatchOverlay;

// The secret helper (SecretCandidates): beside the opponent's hero while they have a hidden Secret, the Secrets their class could
// have, with the ones ruled out dimmed. A candidate is ruled out when the player's action would have set it off and the Secret stayed
// hidden. The engine answers "would it have": before each of the player's actions, on a copy of the game with the hidden Secret
// swapped for the candidate (NativeMatch calls Before and After around the real action). The pool is public: the Wild Secrets of
// the opponent's class. A candidate whose trigger depends on the opponent's hidden cards (their hand or deck) can be ruled out by
// what only the engine sees; that is rare for Secrets. It fails closed on its own: an error turns only the helper off for the session.
internal static class NativeSecretHelper
{
    private static SecretCandidates candidates = new SecretCandidates();
    private static bool failed;
    private static string lastSummary;
    private static List<SecretRow> rows = new List<SecretRow>();
    private static GUIStyle title;
    private static int styleSize;
    private static Texture2D back, edge;
    private static BepInEx.Logging.ManualLogSource log;

    internal static bool Failed => failed;
    internal static void Initialize(BepInEx.Logging.ManualLogSource logger) => log = logger;
    internal static void Forget() { candidates = new SecretCandidates(); rows = new List<SecretRow>(); lastSummary = null; }
    private static bool On => !failed && MatchOverlay.Settings != null && MatchOverlay.Settings.SecretHelper;

    private static void Fail(Exception error)
    {
        failed = true; Forget();
        log?.LogWarning("SECRET_HELPER off for the session: " + error);
    }

#if ENGINE_SOURCE
    private static readonly Dictionary<SabberStoneCore.Enums.CardClass, List<SecretOption>> pools =
        new Dictionary<SabberStoneCore.Enums.CardClass, List<SecretOption>>();

    // Called by NativeMatchTracker when the board settles: start tracking new hidden Secrets, forget revealed ones.
    internal static void Observe(SabberStoneCore.Model.Game game)
    {
        if (!On) return;
        try { Sync(game); }
        catch (Exception error) { Fail(error); }
    }

    // Before the player's action: the candidates it would set off, by hidden Secret. Null when there is nothing to test.
    internal static List<KeyValuePair<int, string>> Before(SabberStoneCore.Model.Game game, SabberStoneCore.Tasks.PlayerTasks.PlayerTask task)
    {
        if (!On || game == null || task == null || task.Controller != game.Player1
            || task is SabberStoneCore.Tasks.PlayerTasks.ConcedeTask) return null;
        try
        {
            var hidden = Sync(game);
            if (hidden.Count == 0) return null;
            string print = task.FullPrint();
            var fired = new List<KeyValuePair<int, string>>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int tried = 0, unmatched = 0;
            foreach (var secret in hidden)
                foreach (string card in candidates.Possible(secret.Id).ToList())
                {
                    tried++;
                    bool? would = WouldFire(game, secret.Id, card, print);
                    if (would == null) unmatched++;
                    else if (would == true) fired.Add(new KeyValuePair<int, string>(secret.Id, card));
                }
            log?.LogInfo("SECRET_HELPER tested " + tried + " candidates for " + task.PlayerTaskType + " in " + watch.ElapsedMilliseconds
                + " ms: " + fired.Count + " would fire" + (unmatched > 0 ? ", " + unmatched + " not replayed" : ""));
            return fired;
        }
        catch (Exception error) { Fail(error); return null; }
    }

    // After the real action: the candidates it would have set off are ruled out for each Secret that is still hidden.
    internal static void After(SabberStoneCore.Model.Game game, List<KeyValuePair<int, string>> fired)
    {
        if (fired == null || fired.Count == 0 || !On) return;
        try
        {
            Sync(game);
            var out_ = new List<string>();
            foreach (var pair in fired)
                if (candidates.RuleOut(pair.Key, pair.Value)) out_.Add(pair.Key + ":" + pair.Value);
            if (out_.Count > 0) log?.LogInfo("SECRET_HELPER ruled out " + string.Join(" ", out_));
            Publish();
        }
        catch (Exception error) { Fail(error); }
    }

    private static List<SabberStoneCore.Model.Entities.Spell> Sync(SabberStoneCore.Model.Game game)
    {
        var them = game.Player2;
        var hidden = them.SecretZone.Where(s => s.Card.IsSecret && !s.Card.IsQuest && s[SabberStoneCore.Enums.GameTag.REVEALED] == 0).ToList();
        candidates.Keep(hidden.Select(s => s.Id).ToList());
        foreach (var secret in hidden) if (!candidates.Tracks(secret.Id)) candidates.Track(secret.Id, Pool(them.HeroClass));
        Publish();
        return hidden;
    }

    private static void Publish()
    {
        rows = candidates.Rows();
        string summary = candidates.Summary();
        if (summary != lastSummary) { lastSummary = summary; log?.LogInfo("SECRET_HELPER " + (summary.Length == 0 ? "none" : summary)); }
    }

    // The Wild Secrets of a class that the engine can play: collectible, with a trigger.
    private static List<SecretOption> Pool(SabberStoneCore.Enums.CardClass cls)
    {
        if (pools.TryGetValue(cls, out var pool)) return pool;
        pool = new List<SecretOption>();
        if (SabberStoneCore.Model.Cards.Wild.TryGetValue(cls, out var cards))
            foreach (var card in cards)
                if (card.Collectible && card.IsSecret && !card.IsQuest && card.Power?.Trigger != null && pool.All(o => o.Name != card.Name))
                    pool.Add(new SecretOption { Id = card.Id, Name = card.Name, Cost = card.Cost,
                        Legendary = card.Rarity == SabberStoneCore.Enums.Rarity.LEGENDARY });
        return pools[cls] = pool;
    }

    // On a copy of the game: the hidden Secret swapped for the candidate, then the player's action. Null when the copy offers no
    // matching action (it is not replayed, so nothing is ruled out).
    private static bool? WouldFire(SabberStoneCore.Model.Game game, int secretId, string cardId, string print)
    {
        var copy = game.CloneForSimulation(0x5EC4E7L ^ game.Turn);
        if (!(copy.IdEntityDic.TryGetValue(secretId, out var entity) && entity is SabberStoneCore.Model.Entities.Spell hidden)) return null;
        var owner = hidden.Controller;
        int order = hidden.OrderOfPlay;
        bool exhausted = hidden.IsExhausted;
        hidden.ActivatedTrigger?.Remove();
        owner.SecretZone.Remove(hidden);
        var stand = (SabberStoneCore.Model.Entities.Spell)SabberStoneCore.Model.Entities.Entity.FromCard(owner, SabberStoneCore.Model.Cards.FromId(cardId));
        stand.Power?.Trigger?.Activate(stand);
        owner.SecretZone.Add(stand);
        stand.IsExhausted = exhausted;
        stand.OrderOfPlay = order;
        var player = copy.CurrentPlayer;
        var mirror = player?.Options().FirstOrDefault(option => option.FullPrint() == print);
        if (mirror == null) return null;
        copy.Process(mirror);
        return stand.Zone == null || stand.Zone.Type != SabberStoneCore.Enums.Zone.SECRET || stand[SabberStoneCore.Enums.GameTag.REVEALED] == 1;
    }
#endif

    // Each frame: beside the opponent's hero while they have a hidden Secret, a heading and the candidates as the deck tracker's card
    // tiles, the ruled-out ones dimmed. Hovering a tile shows the full card beside the list (NativeTrackerPreview). A long list shrinks
    // its tiles to stay on screen.
    internal static void Draw()
    {
        if (!On || rows.Count == 0) return;
        try
        {
            var hero = GameState.Get()?.GetOpposingSidePlayer()?.GetHeroCard();
            var heroRect = NativeCardChips.CardRect(hero);
            if (heroRect.width <= 0) return;
            int size = Mathf.Max(8, Mathf.RoundToInt(Screen.height * NativeMatchTracker.TextShare * .62f));
            Styles(size);
            int left = rows.Count(r => r.Possible);
            string heading = candidates.Hidden == 1 ? "Secret: " + left + " possible" : candidates.Hidden + " Secrets: " + left + " possible";
            if (left == 0) heading = candidates.Hidden == 1 ? "Secret: not from their class" : candidates.Hidden + " Secrets: not from their class";
            float line = size * 1.35f, pad = size * .55f;
            float row = Mathf.Min(Screen.height * NativeMatchTracker.RowShare, (Screen.height * .92f - line - pad * 2f) / rows.Count);
            float width = Mathf.Max(Measure(heading, title).x, NativeMatchTracker.TileWidth(row, rows.Select(r => r.Option.Name)));
            var box = new Rect(heroRect.xMin - width - pad * 3f, heroRect.yMin, width + pad * 2f, line + row * rows.Count + pad * 2f);
            box.x = Mathf.Max(0f, box.x);
            if (box.yMax > Screen.height) box.y = Mathf.Max(0f, Screen.height - box.height);
            GUI.DrawTexture(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), edge);
            GUI.DrawTexture(box, back);
            Text(new Rect(box.x + pad, box.y + pad, width, line), heading, title, Align.Left);
            float top = box.y + pad + line;
            SecretRow hovered = null; Rect hoveredArea = Rect.zero;
            foreach (var secret in rows)
            {
                var area = new Rect(box.x + pad, top, width, row);
                NativeMatchTracker.Tile(area.x, area.y, area.width, row, secret.Option.Id, secret.Option.Cost.ToString(), secret.Option.Name, 1,
                    secret.Option.Legendary, dim: !secret.Possible);
                if (area.Contains(Event.current.mousePosition)) { hovered = secret; hoveredArea = area; }
                top += row;
            }
            // On the list's left, where the board is open; on its right (over the hero) when the screen's edge is too close.
            if (hovered != null) NativeTrackerPreview.Show(hovered.Option.Id, hoveredArea, box, box.x >= NativeTrackerPreview.Width);
        }
        catch (Exception error) { Fail(error); }
    }

    private static void Styles(int size)
    {
        if (title != null && styleSize == size) return;
        back = back ?? Solid(new Color(.05f, .06f, .09f, .92f));
        edge = edge ?? Solid(new Color(.78f, .64f, .36f, .95f));
        title = MatchOverlay.Label(size, new Color(1f, .85f, .5f));
        styleSize = size;
    }
}
