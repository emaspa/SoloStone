using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using static MatchOverlay;

// The Stats screen: how each deck has done, from the profile's settled matches (LocalProfile.DeckStatsBook, DeckStats). It opens
// from a Stats button in the main screen's drawer, a copy of the Collection slot that NativeHomeUI.LayoutDrawer places after
// Collection; the copy's icon is replaced by a drawn deck of cards (DeckIcon). The screen itself is IMGUI over the main screen, with an input blocker
// under it so clicks don't reach the box: the deck list on the left, the chosen deck on the right (its record overall, by mode and
// by opponent class, its cards' win rates over card art tiles, its recent games). Close, Escape or leaving the main screen closes
// it. Every path fails closed: a broken button leaves the drawer as it was, a broken screen closes with one warning.
internal static class NativeStatsScreen
{
    internal const string SlotName = "Box_Button_SoloStoneStats", ButtonLabel = "Stats", IconName = "SoloStoneStatsIcon";
    private static ManualLogSource log;
    private static Transform slot;
    private static bool slotFailed;
    private static bool open;
    private static IReadOnlyList<DeckStatsEntry> book;
    private static int selected;
    private static float listScroll, detailScroll, listHeight, detailHeight;
    private static GameObject blocker;

    internal static bool IsOpen => open;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        harmony.Patch(AccessTools.Method(typeof(BnetBar), "HandleEscapeKey") ?? throw new MissingMethodException("BnetBar", "HandleEscapeKey"),
            prefix: new HarmonyMethod(typeof(NativeStatsScreen), nameof(BeforeEscape)));
    }

    // HarmonyX runs every prefix: a key an earlier one took (NativeReportUI) is not handled again.
    private static bool BeforeEscape(ref bool __result, bool __runOriginal)
    {
        if (!__runOriginal) return false;
        if (!open) return true;
        Close("escape");
        __result = true;
        return false;
    }

    // The drawer slot, made once per box from the Collection slot; null when it can't be made.
    internal static Transform Slot(Box box, Transform collection, Action<string> report)
    {
        if (slotFailed || collection == null) return null;
        if (slot != null && slot.parent == collection.parent) return slot;
        try
        {
            Transform existing = collection.parent.Find(SlotName);
            if (existing != null) return slot = existing;
            GameObject clone = UnityEngine.Object.Instantiate(collection.gameObject, collection.parent, false);
            clone.name = SlotName;
            var button = clone.GetComponentInChildren<BoxMenuButton>(true) ?? throw new InvalidOperationException("no BoxMenuButton in the Collection slot");
            button.ClearEventListeners();
            button.AddEventListener(UIEventType.RELEASE, _ => Open());
            Relabel(clone.transform);
            string icon = ReplaceIcon(clone.transform, button);
            slot = clone.transform;
            report?.Invoke("STATS_SCREEN button made from " + collection.name + " icon=" + icon + " children=" + string.Join(",", Children(clone.transform)));
            return slot;
        }
        catch (Exception error)
        {
            slotFailed = true;
            log?.LogWarning("STATS_SCREEN no drawer button: " + error.Message);
            if (slot != null) UnityEngine.Object.Destroy(slot.gameObject);
            slot = null;
            return null;
        }
    }

    private static IEnumerable<string> Children(Transform root)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            var renderer = child.GetComponent<Renderer>();
            yield return child.name + (renderer == null ? "" : "[" + renderer.GetType().Name + (renderer.enabled ? "" : ",off") + "]");
        }
    }

    // The slot's caption (UberText, which the plugin doesn't reference) reads Stats.
    private static void Relabel(Transform root)
    {
        int changed = 0;
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component.GetType().Name != "UberText") continue;
            var text = component.GetType().GetProperty("Text");
            if (text == null || !text.CanWrite) continue;
            text.SetValue(component, ButtonLabel, null);
            changed++;
        }
        if (changed == 0) throw new InvalidOperationException("no caption");
    }

    // The Collection art makes way for a drawn deck of cards: a quad the size of the old icon, facing the camera, with a copy of the
    // old icon's material showing the DeckIcon texture. Returns what it did, for the log. On failure the Collection art stays.
    private static string ReplaceIcon(Transform root, BoxMenuButton button)
    {
        // Live 2026-11-09: the Collection slot leaves m_ButtonMeshRenderer empty; its art is the CollectionButton mesh, with a
        // shield-shaped shadow mesh under it. The icon is the largest enabled mesh that is neither caption, highlight nor shadow.
        Renderer old = button.m_ButtonMeshRenderer ?? root.GetComponentsInChildren<MeshRenderer>(true)
            .Where(renderer => renderer.enabled && !Named(renderer, "Highlight") && !Named(renderer, "Shadow") && !Named(renderer, "ButtonName"))
            .OrderByDescending(renderer => renderer.bounds.size.sqrMagnitude).FirstOrDefault();
        if (old == null) return "kept (no icon renderer)";
        try
        {
            Camera view = CameraUtils.FindFirstByLayerMask(1 << old.gameObject.layer) ?? throw new InvalidOperationException("no camera");
            Bounds bounds = old.bounds;
            Vector3 size = bounds.size;
            Vector3 normal = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
            Vector3 right = Flatten(view.transform.right, normal), up = Flatten(view.transform.up, normal);
            float width = Mathf.Abs(Vector3.Dot(size, Abs(right))), height = Mathf.Abs(Vector3.Dot(size, Abs(up)));
            float side = Mathf.Min(width, height) * .5f;
            Vector3 centre = bounds.center + (view.transform.position - bounds.center).normalized * .01f;
            var quad = new GameObject(IconName) { layer = old.gameObject.layer };
            quad.transform.SetParent(old.transform, false);
            quad.transform.localPosition = Vector3.zero; quad.transform.localRotation = Quaternion.identity; quad.transform.localScale = Vector3.one;
            Vector3 Local(Vector3 world) => quad.transform.InverseTransformPoint(world);
            var mesh = new Mesh {
                vertices = new[] { Local(centre - right * side - up * side), Local(centre + right * side - up * side),
                    Local(centre + right * side + up * side), Local(centre - right * side + up * side) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }
            };
            mesh.RecalculateBounds();
            quad.AddComponent<MeshFilter>().sharedMesh = mesh;
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            // The Collection art's shader (Hero/Unlit/Unlit_TextureColor) is opaque, which drew the icon on a black square. Unity's
            // Sprites/Default blends by alpha; the client looks it up itself, so it ships in the build.
            Shader blended = Shader.Find("Sprites/Default");
            var material = blended != null ? new Material(blended) : new Material(old.sharedMaterial);
            material.mainTexture = DeckIcon.Texture; material.mainTextureScale = Vector2.one; material.mainTextureOffset = Vector2.zero;
            quad.AddComponent<MeshRenderer>().sharedMaterial = material;
            old.enabled = false;
            foreach (var shadow in root.GetComponentsInChildren<MeshRenderer>(true)) if (Named(shadow, "Shadow")) shadow.enabled = false;
            return "deck icon from " + DeckIcon.Source + " on " + old.name + " (shader " + material.shader?.name + ", queue " + material.renderQueue + ", normal " + normal + ")";
        }
        catch (Exception error) { return "kept (" + error.Message + ")"; }
    }

    // The direction along the plane with this normal (unit length); the client's Unity has no ProjectOnPlane.
    private static Vector3 Flatten(Vector3 direction, Vector3 normal) => (direction - normal * Vector3.Dot(direction, normal)).normalized;

    private static bool Named(Component component, string part)
    {
        for (Transform at = component.transform; at != null; at = at.parent)
        {
            if (at.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (at.name == SlotName) break;
        }
        return false;
    }

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    internal static void Open()
    {
        try
        {
            var profile = LocalLadder.Profile ?? throw new InvalidOperationException("no profile");
            book = profile.DeckStatsBook();
            selected = 0; listScroll = detailScroll = 0;
            var box = Box.Get();
            Camera view = box == null ? null : CameraUtils.FindFirstByLayerMask(1 << box.gameObject.layer);
            if (view != null && blocker == null)
            {
                blocker = CameraUtils.CreateInputBlocker(view, "SoloStoneStatsInputBlocker", box, box, 10f);
                blocker.layer = box.gameObject.layer;
            }
            open = true;
            log?.LogInfo("STATS_SCREEN open decks=" + book.Count + " blocker=" + (blocker != null));
        }
        catch (Exception error) { log?.LogWarning("STATS_SCREEN can't open: " + error.Message); Close("error"); }
    }

    internal static void Close(string why)
    {
        open = false; book = null;
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        try { CardArt.Clear(); } catch (Exception) { }
        log?.LogInfo("STATS_SCREEN closed (" + why + ")");
    }

    // Called from OfflineProbe.Update: the screen belongs to the main screen.
    internal static void Tick()
    {
        if (!open) return;
        var scenes = SceneMgr.Get();
        if (scenes == null || scenes.GetMode() != SceneMgr.Mode.HUB) Close("left the main screen");
    }

    // Drawing. Sizes are shares of the screen height, like the deck tracker's.
    private static GUIStyle title, heading, body, small, rate, button, hidden;
    private static int styleLine;
    private static Texture2D backdrop, panel, border, rowFill, rowSelected, chip;
    private static readonly Color Gold = new Color(.86f, .7f, .36f);

    private static void Styles(int line)
    {
        if (title != null && styleLine == line) return;
        backdrop = backdrop ?? Solid(new Color(0f, 0f, 0f, .62f));
        panel = panel ?? Solid(new Color(.06f, .07f, .1f, .97f));
        border = border ?? Solid(Gold);
        rowFill = rowFill ?? Solid(new Color(.1f, .11f, .15f, 1f));
        rowSelected = rowSelected ?? Solid(new Color(.2f, .25f, .38f, 1f));
        chip = chip ?? Solid(new Color(.1f, .11f, .15f, 1f));
        title = Label(Mathf.RoundToInt(line * 1.15f), new Color(1f, .9f, .65f));
        heading = Label(Mathf.RoundToInt(line * .8f), Gold);
        body = Label(Mathf.RoundToInt(line * .66f), new Color(.93f, .93f, .95f));
        small = Label(Mathf.RoundToInt(line * .55f), new Color(.75f, .78f, .84f));
        rate = Label(Mathf.RoundToInt(line * .64f), Color.white);
        button = Label(Mathf.RoundToInt(line * .7f), Color.white);
        hidden = new GUIStyle();
        styleLine = line;
    }

    // Called from OfflineProbe.OnGUI.
    internal static void Draw()
    {
        if (!open) return;
        try
        {
            float h = Screen.height, w = Screen.width, line = h * NativeMatchTracker.TextShare;
            Styles(Mathf.Max(8, Mathf.RoundToInt(line)));
            var current = Event.current;
            GUI.DrawTexture(new Rect(0, 0, w, h), backdrop);
            float pw = Mathf.Min(w * .94f, h * 1.8f), ph = h * .86f, px = (w - pw) / 2, py = (h - ph) / 2, pad = line * .8f;
            var frame = new Rect(px, py, pw, ph);
            GUI.DrawTexture(frame, panel);
            Outline(frame, Mathf.Max(2f, line * .08f));
            Text(new Rect(px + pad, py + pad * .6f, pw, line * 1.6f), "Deck Stats", title, Align.Left);
            var close = new Rect(px + pw - pad - line * 4.2f, py + pad * .7f, line * 4.2f, line * 1.4f);
            if (Button(close, NativeStrings.Get("GLOBAL_BUTTON_CLOSE", "Close"))) { Close("close button"); return; }

            float top = py + pad * .6f + line * 2.1f, bottom = py + ph - pad;
            if (book == null || book.Count == 0)
            {
                Text(new Rect(px + pad, top, pw - pad * 2, line * 1.4f), "No finished games yet. Play a match and your decks show up here.", body, Align.Left);
                return;
            }
            selected = Mathf.Clamp(selected, 0, book.Count - 1);
            var list = new Rect(px + pad, top, pw * .27f, bottom - top);
            var detail = new Rect(list.xMax + pad, top, px + pw - pad - list.xMax - pad, bottom - top);
            if (current.type == EventType.ScrollWheel)
            {
                float step = current.delta.y * line * .9f;
                if (list.Contains(current.mousePosition)) { listScroll = Mathf.Clamp(listScroll + step, 0, Mathf.Max(0, listHeight - list.height)); current.Use(); }
                else if (detail.Contains(current.mousePosition)) { detailScroll = Mathf.Clamp(detailScroll + step, 0, Mathf.Max(0, detailHeight - detail.height)); current.Use(); }
            }
            DeckList(list, line);
            Detail(detail, book[selected], line);
        }
        catch (Exception error)
        {
            log?.LogWarning("STATS_SCREEN closed after an error: " + error);
            Close("error");
        }
    }

    private static void DeckList(Rect area, float line)
    {
        GUI.BeginGroup(area, new GUIContent(), hidden);
        try
        {
            float y = -listScroll, rowHeight = line * 2.3f;
            for (int i = 0; i < book.Count; i++)
            {
                var entry = book[i];
                var row = new Rect(0, y, area.width, rowHeight - line * .2f);
                GUI.DrawTexture(row, i == selected ? rowSelected : rowFill);
                Text(new Rect(row.x + line * .5f, row.y + line * .15f, row.width - line, line * 1.05f), entry.Name, body, Align.Left);
                var stats = entry.Stats;
                Text(new Rect(row.x + line * .5f, row.y + line * 1.1f, row.width - line, line * .9f),
                    DeckStatsText.ClassName(entry.Class) + "   " + DeckStatsText.Record(stats.Wins, stats.Losses), small, Align.Left);
                if (GUI.Button(row, "", hidden) && i != selected) { selected = i; detailScroll = 0; CardArt.Clear(); }
                y += rowHeight;
            }
            listHeight = y + listScroll;
        }
        finally { GUI.EndGroup(); }
    }

    private static void Detail(Rect area, DeckStatsEntry entry, float line)
    {
        GUI.BeginGroup(area, new GUIContent(), hidden);
        try
        {
            var stats = entry.Stats;
            float y = -detailScroll, width = area.width;
            Text(new Rect(0, y, width, line * 1.3f), entry.Name, heading, Align.Left);
            y += line * 1.3f;
            string modes = "Ranked " + stats.RankedWins + "–" + stats.RankedLosses + "     Casual " + stats.CasualWins + "–" + stats.CasualLosses;
            Text(new Rect(0, y, width, line), DeckStatsText.ClassName(entry.Class) + "     " + DeckStatsText.Record(stats.Wins, stats.Losses) + "     " + modes, body, Align.Left);
            y += line * 1.6f;

            // Against each class: chips, four to a row.
            Text(new Rect(0, y, width, line), "Against each class", heading, Align.Left);
            y += line * 1.2f;
            var classes = stats.ByClass.OrderByDescending(pair => pair.Value[0] + pair.Value[1]).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToList();
            if (classes.Count == 0) { Text(new Rect(0, y, width, line), "No opponents recorded yet.", small, Align.Left); y += line * 1.2f; }
            float chipWidth = (width - line * .6f) / 4, chipHeight = line * 1.3f;
            for (int i = 0; i < classes.Count; i++)
            {
                var record = classes[i].Value;
                var box = new Rect((i % 4) * (chipWidth + line * .2f), y + (i / 4) * (chipHeight + line * .2f), chipWidth, chipHeight);
                GUI.DrawTexture(box, chip);
                Text(new Rect(box.x + line * .4f, box.y, box.width - line * .8f, box.height), DeckStatsText.ClassName(classes[i].Key) + "  " + record[0] + "–" + record[1], body, Align.Left);
                Shaded(new Rect(box.x + line * .4f, box.y, box.width - line * .8f, box.height), record[0], record[0] + record[1], Align.Right);
            }
            if (classes.Count > 0) y += ((classes.Count + 3) / 4) * (chipHeight + line * .2f) + line * .4f;

            // The cards: an art tile and four rates each.
            Text(new Rect(0, y, width, line), NativeStrings.Get("GLUE_STORE_MODE_CARDS", "Cards"), heading, Align.Left);
            Text(new Rect(0, y, width, line), stats.TrackedGames == 0 ? "card win rates start with your next game"
                : "over " + stats.TrackedGames + (stats.TrackedGames == 1 ? " game" : " games") + " with card stats", small, Align.Right);
            y += line * 1.2f;
            float tileWidth = Mathf.Min(width * .42f, line * 13f), columns = (width - tileWidth - line * .5f) / 5f, tileHeight = line * 1.35f;
            string[] names = { "Mulligan", "Kept", "Drawn", "Played", "Games played" };
            for (int c = 0; c < names.Length; c++)
                Text(new Rect(tileWidth + line * .5f + columns * c, y, columns, line), names[c], small, Align.Center);
            y += line * 1.05f;
            foreach (var card in Cards(entry))
            {
                var stat = stats.For(card.Id);
                if (y + tileHeight > 0 && y < area.height)
                {
                    NativeMatchTracker.Tile(0, y, tileWidth, tileHeight, card.Id, card.Cost.ToString(), card.Name, card.Copies, card.Legendary);
                    float x = tileWidth + line * .5f;
                    Shaded(new Rect(x, y, columns, tileHeight), stat?.OpeningWins ?? 0, stat?.OpeningGames ?? 0, Align.Center);
                    Plain(new Rect(x + columns, y, columns, tileHeight), stat == null || stat.OfferedGames == 0 ? "–" : stat.KeptGames + " of " + stat.OfferedGames);
                    Shaded(new Rect(x + columns * 2, y, columns, tileHeight), stat?.DrawnWins ?? 0, stat?.DrawnGames ?? 0, Align.Center);
                    Shaded(new Rect(x + columns * 3, y, columns, tileHeight), stat?.PlayedWins ?? 0, stat?.PlayedGames ?? 0, Align.Center);
                    Plain(new Rect(x + columns * 4, y, columns, tileHeight), (stat?.PlayedGames ?? 0).ToString());
                }
                y += tileHeight;
            }
            y += line * .8f;

            // Recent games, newest first.
            Text(new Rect(0, y, width, line), "Recent games", heading, Align.Left);
            y += line * 1.2f;
            foreach (var game in stats.Recent)
            {
                bool won = game.Result == "Won";
                rate.normal.textColor = won ? new Color(.55f, .9f, .55f) : new Color(1f, .5f, .45f);
                Text(new Rect(0, y, line * 3f, line), DeckStatsText.Result(game.Result), rate, Align.Left);
                rate.normal.textColor = Color.white;
                string against = game.Cards?.OpponentClass ?? game.OpponentClass;
                string text = (string.IsNullOrEmpty(against) ? "" : "vs " + DeckStatsText.ClassName(against))
                    + (string.IsNullOrEmpty(game.Opponent) ? "" : " (" + game.Opponent + ")") + "   " + game.Turns + " turns   " + (game.Casual ? "Casual" : "Ranked");
                Text(new Rect(line * 3f, y, width - line * 7f, line), text, body, Align.Left);
                Text(new Rect(0, y, width, line), DeckStatsText.Day(game.Settled), small, Align.Right);
                y += line * 1.15f;
            }
            detailHeight = y + detailScroll;
        }
        finally { GUI.EndGroup(); }
    }

    private struct CardRow { internal string Id, Name; internal int Cost, Copies; internal bool Legendary; }

    // The deck's cards, one row per card with its copies, cheapest first.
    private static IEnumerable<CardRow> Cards(DeckStatsEntry entry) =>
        (entry.Cards ?? new List<string>()).GroupBy(id => id, StringComparer.Ordinal).Select(group => {
            var card = SabberStoneCore.Model.Cards.FromId(group.Key);
            return new CardRow { Id = group.Key, Name = card?.Name ?? group.Key, Cost = card?.Cost ?? 0, Copies = group.Count(),
                Legendary = card?.Rarity == SabberStoneCore.Enums.Rarity.LEGENDARY };
        }).OrderBy(row => row.Cost).ThenBy(row => row.Name, StringComparer.Ordinal);

    private static void Shaded(Rect area, int wins, int games, Align align)
    {
        float[] shade = DeckStatsText.Shade(wins, games);
        rate.normal.textColor = new Color(shade[0], shade[1], shade[2]);
        Text(area, DeckStatsText.Rate(wins, games), rate, align);
        rate.normal.textColor = Color.white;
    }

    private static void Plain(Rect area, string text) => Text(area, text, body, Align.Center);

    private static bool Button(Rect area, string text)
    {
        GUI.DrawTexture(area, border);
        GUI.DrawTexture(new Rect(area.x + 2, area.y + 2, area.width - 4, area.height - 4), rowSelected);
        Text(area, text, button, Align.Center);
        return GUI.Button(area, "", hidden);
    }

    private static void Outline(Rect area, float thick)
    {
        GUI.DrawTexture(new Rect(area.x, area.y, area.width, thick), border);
        GUI.DrawTexture(new Rect(area.x, area.yMax - thick, area.width, thick), border);
        GUI.DrawTexture(new Rect(area.x, area.y, thick, area.height), border);
        GUI.DrawTexture(new Rect(area.xMax - thick, area.y, thick, area.height), border);
    }
}


// The Stats button's icon: a fanned deck of three cards, drawn once into a texture in the drawer's painted style (a dark outline
// round each card, a soft drop shadow, a transparent background). The card faces are the game's default card back
// (CardBackManager's DEFAULT slot, loaded at startup), read back from the GPU since the texture itself isn't readable; while it
// isn't loaded, a plain crimson back with a gold diamond stands in. Card edges are sampled four times per pixel.
internal static class DeckIcon
{
    private static Texture2D texture;
    private static Color[] face;
    private static int faceWidth, faceHeight;
    internal const int Size = 256;
    internal static string Source = "not drawn";

    internal static Texture2D Texture
    {
        get
        {
            if (texture != null) return texture;
            try { Source = ReadCardBack(); }
            catch (Exception error) { face = null; Source = "drawn back (" + error.Message + ")"; }
            texture = new Texture2D(Size, Size) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(Compose());
            texture.Apply();
            return texture;
        }
    }

    // The default card back's art, as pixels; what was used, for the log.
    private static string ReadCardBack()
    {
        CardBack back = CardBackManager.Get()?.GetCardBackBySlot(CardBackManager.CardBackSlot.DEFAULT);
        Texture source = back == null ? null : back.m_CardBackTexture != null ? back.m_CardBackTexture
            : back.m_CardBackMaterial != null ? back.m_CardBackMaterial.mainTexture : null;
        if (source == null) { face = null; return "drawn back (default card back not loaded)"; }
        faceWidth = Mathf.Clamp(source.width, 16, 512);
        faceHeight = Mathf.Clamp(Mathf.RoundToInt(faceWidth * (float)source.height / source.width), 16, 512);
        RenderTexture target = RenderTexture.GetTemporary(faceWidth, faceHeight), previous = RenderTexture.active;
        var copy = new Texture2D(faceWidth, faceHeight) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            Graphics.Blit(source, target, Vector2.one, Vector2.zero);
            RenderTexture.active = target;
            copy.ReadPixels(new Rect(0, 0, faceWidth, faceHeight), 0, 0);
            copy.Apply();
            face = copy.GetPixels();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(copy);
        }
        return "card back " + source.name + " " + source.width + "x" + source.height;
    }

    // The cards, back to front: their tilt in degrees and how much darker they are.
    private static readonly float[] Tilts = { 17f, -17f, 0f }, Shades = { .68f, .8f, 1f };
    private const float HalfHeight = .3f, Corner = .035f, Outline = .016f, PivotU = .5f, PivotV = .06f, CentreV = .47f;
    private static readonly Color Ink = new Color(.12f, .07f, .03f, 1f);

    private static Color[] Compose()
    {
        float aspect = face != null ? Mathf.Clamp(FaceUv.width * faceWidth / (FaceUv.height * faceHeight), .55f, .85f) : .7f;
        float halfWidth = HalfHeight * aspect;
        var cards = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                Color sum = Color.clear;
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                    {
                        Color c = Sample((x + .25f + sx * .5f) / Size, (y + .25f + sy * .5f) / Size, halfWidth);
                        sum += new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a); // premultiplied, so edges don't fringe
                    }
                cards[y * Size + x] = sum * .25f;
            }
        // The drop shadow: the cards' outline, blurred, shifted down and right, under them.
        int dx = Size / 64, dy = Size / 48;
        var shadow = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int from = (y + dy) * Size + (x - dx);
                shadow[y * Size + x] = y + dy < Size && x - dx >= 0 ? cards[from].a : 0f;
            }
        shadow = Blur(Blur(shadow, Size / 64), Size / 64);
        var pixels = new Color[Size * Size];
        for (int i = 0; i < pixels.Length; i++)
        {
            Color top = cards[i];
            float under = shadow[i] * .6f * (1f - top.a);
            float alpha = top.a + under;
            pixels[i] = alpha <= 0f ? Color.clear : new Color(top.r / alpha, top.g / alpha, top.b / alpha, alpha);
        }
        return pixels;
    }

    // A box blur of the given radius, across then down.
    private static float[] Blur(float[] values, int radius)
    {
        var across = new float[values.Length];
        var down = new float[values.Length];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float sum = 0; int count = 0;
                for (int k = -radius; k <= radius; k++) { int at = x + k; if (at < 0 || at >= Size) continue; sum += values[y * Size + at]; count++; }
                across[y * Size + x] = sum / count;
            }
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float sum = 0; int count = 0;
                for (int k = -radius; k <= radius; k++) { int at = y + k; if (at < 0 || at >= Size) continue; sum += across[at * Size + x]; count++; }
                down[y * Size + x] = sum / count;
            }
        return down;
    }

    // u, v in 0..1, v up. The front-most card under the point decides its colour.
    private static Color Sample(float u, float v, float halfWidth)
    {
        for (int i = Tilts.Length - 1; i >= 0; i--)
        {
            // Into the card's own frame: turn about the pivot below the deck, then measure from the card's centre.
            double angle = Tilts[i] * Math.PI / 180; float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            float du = u - PivotU, dv = v - PivotV;
            float lu = du * cos - dv * sin, lv = du * sin + dv * cos + PivotV - CentreV;
            Color? card = Card(lu, lv, halfWidth, Shades[i]);
            if (card.HasValue) return card.Value;
        }
        return Color.clear;
    }

    // One card at (x, y) from its centre; null outside it.
    private static Color? Card(float x, float y, float halfWidth, float shade)
    {
        float dx = Mathf.Max(Mathf.Abs(x) - (halfWidth - Corner), 0f), dy = Mathf.Max(Mathf.Abs(y) - (HalfHeight - Corner), 0f);
        float edge = Mathf.Sqrt(dx * dx + dy * dy) - Corner; // < 0 inside the rounded card
        if (edge > 0f) return null;
        if (edge > -Outline) return Ink;
        Color c = Face((x + halfWidth) / (2 * halfWidth), (y + HalfHeight) / (2 * HalfHeight));
        return new Color(c.r * shade, c.g * shade, c.b * shade, 1f);
    }

    // The card back texture is an atlas: the face in the middle, the card's edge strips either side (seen 2026-11-09 in the default
    // back, GenFX_Card_Back_Default_D, 512x512). This is the face, in texture coordinates, v up.
    private static readonly Rect FaceUv = new Rect(.18f, .06f, .64f, .915f);

    // The card's face at u, v (0..1 across it): the card back's art, or the stand-in.
    private static Color Face(float u, float v)
    {
        if (face != null)
        {
            u = FaceUv.x + u * FaceUv.width; v = FaceUv.y + v * FaceUv.height;
            float fx = Mathf.Clamp(u * faceWidth - .5f, 0, faceWidth - 1), fy = Mathf.Clamp(v * faceHeight - .5f, 0, faceHeight - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, faceWidth - 1), y1 = Math.Min(y0 + 1, faceHeight - 1);
            float tx = fx - x0, ty = fy - y0;
            Color low = Color.Lerp(face[y0 * faceWidth + x0], face[y0 * faceWidth + x1], tx);
            Color high = Color.Lerp(face[y1 * faceWidth + x0], face[y1 * faceWidth + x1], tx);
            return Color.Lerp(low, high, ty);
        }
        float cx = u * 2 - 1, cy = v * 2 - 1;
        if (Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cy)) > .86f) return Color.Lerp(new Color(.62f, .45f, .16f), new Color(.95f, .8f, .42f), v);
        float diamond = Mathf.Abs(cx) / .45f + Mathf.Abs(cy) / .45f;
        if (diamond < 1f) return diamond > .78f ? new Color(.95f, .8f, .42f) : Color.Lerp(new Color(.18f, .32f, .62f), new Color(.1f, .18f, .4f), diamond);
        return Color.Lerp(new Color(.52f, .1f, .1f), new Color(.3f, .05f, .07f), Mathf.Clamp01(Mathf.Sqrt(cx * cx + cy * cy) / 1.2f));
    }
}
