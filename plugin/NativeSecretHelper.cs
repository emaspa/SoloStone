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
    private static GUIStyle title, name, dim, detail;
    private static int styleSize;
    private static Texture2D back, edge, strike;
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
                    pool.Add(new SecretOption { Id = card.Id, Name = card.Name, Cost = card.Cost, Text = LocalCollectionUI.RulesText(card) });
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

    // Each frame: the list beside the opponent's hero, while they have a hidden Secret. Hovering a name shows its text.
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
            float line = size * 1.35f, pad = size * .55f, width = Measure(heading, title).x;
            foreach (var row in rows) width = Mathf.Max(width, Measure(Row(row), name).x);
            var box = new Rect(heroRect.xMin - width - pad * 3f, heroRect.yMin, width + pad * 2f, line * (rows.Count + 1) + pad * 2f);
            box.x = Mathf.Max(0f, box.x);
            if (box.yMax > Screen.height) box.y = Mathf.Max(0f, Screen.height - box.height);
            GUI.DrawTexture(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), edge);
            GUI.DrawTexture(box, back);
            float top = box.y + pad;
            Text(new Rect(box.x + pad, top, width, line), heading, title, Align.Left);
            SecretRow hovered = null; Rect hoveredArea = Rect.zero;
            foreach (var row in rows)
            {
                top += line;
                var area = new Rect(box.x + pad, top, width, line);
                string text = Row(row);
                Text(area, text, row.Possible ? name : dim, Align.Left);
                if (!row.Possible)
                    GUI.DrawTexture(new Rect(area.x, Mathf.Round(area.center.y), Measure(text, dim).x, Mathf.Max(1f, size / 12f)), strike);
                if (area.Contains(Event.current.mousePosition)) { hovered = row; hoveredArea = area; }
            }
            if (hovered != null) Detail(hovered, hoveredArea, box);
        }
        catch (Exception error) { Fail(error); }
    }

    private static string Row(SecretRow row) => "(" + row.Option.Cost + ") " + row.Option.Name;

    // The hovered Secret's text, beside the list (on the other side near the screen's left edge).
    private static void Detail(SecretRow row, Rect area, Rect box)
    {
        string text = row.Option.Name + "\n" + Wrap(row.Option.Text, styleSize * 22f) + (row.Possible ? "" : "\nRuled out: your play would have set it off.");
        string[] lines = text.Split('\n');
        float width = 0f, line = styleSize * 1.35f, pad = styleSize * .5f;
        foreach (string piece in lines) width = Mathf.Max(width, Measure(piece, detail).x);
        var tip = new Rect(box.xMax + pad, area.y, width + pad * 2, line * lines.Length + pad * 2);
        if (tip.xMax > Screen.width) tip.x = box.x - tip.width - pad;
        if (tip.yMax > Screen.height) tip.y = Screen.height - tip.height;
        GUI.DrawTexture(new Rect(tip.x - 1f, tip.y - 1f, tip.width + 2f, tip.height + 2f), edge);
        GUI.DrawTexture(tip, back);
        for (int i = 0; i < lines.Length; i++) Text(new Rect(tip.x + pad, tip.y + pad + line * i, width, line), lines[i], detail, Align.Left);
    }

    // Breaks text into lines no wider than width, at spaces.
    private static string Wrap(string text, float width)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string word in (text ?? "").Split(' '))
        {
            string next = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Measure(next, detail).x > width) { lines.Add(current); current = word; }
            else current = next;
        }
        if (current.Length > 0) lines.Add(current);
        return string.Join("\n", lines);
    }

    private static void Styles(int size)
    {
        if (title != null && styleSize == size) return;
        back = back ?? Solid(new Color(.05f, .06f, .09f, .92f));
        edge = edge ?? Solid(new Color(.78f, .64f, .36f, .95f));
        strike = strike ?? Solid(new Color(.6f, .6f, .65f, .8f));
        title = MatchOverlay.Label(size, new Color(1f, .85f, .5f));
        name = MatchOverlay.Label(size, Color.white);
        dim = MatchOverlay.Label(size, new Color(.55f, .56f, .6f));
        detail = MatchOverlay.Label(size, new Color(.92f, .93f, .96f));
        styleSize = size;
    }
}
