using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

// The deck tracker's card art: a band of the card's portrait, drawn through IMGUI. Its left and right edges come from the game's
// own deck list crop (CardDef's deck card bar portrait material, as CollectionDeckTileActor uses it), narrowed to skip unpainted
// white columns (ArtStrip). One card loads per frame, on demand. The client's asset handles (Blizzard.T5.AssetManager, which the
// plugin does not reference) are reached through reflection and held as IDisposable until Clear. A card whose art does not load is
// remembered and drawn without art; a broken loader (the client's API changed) turns the art off for the session with one warning.
internal static class CardArt
{
    internal struct Art { internal Texture Texture; internal Rect Uv; }

    private sealed class Entry
    {
        internal Art Art;
        internal bool Ready, Failed;
        internal DefLoader.DisposableCardDef Def;
        internal readonly List<IDisposable> Handles = new List<IDisposable>();
    }

    private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly Queue<string> wanted = new Queue<string>();
    private static ManualLogSource log;
    private static bool broken;
    private static MethodInfo barPortrait, portraitTexture;

    internal static void Initialize(ManualLogSource logger) { log = logger; }

    // The card's art, or false while it loads or when it has none. Asking queues the load.
    internal static bool TryGet(string id, out Art art)
    {
        art = default;
        if (broken || string.IsNullOrEmpty(id)) return false;
        if (entries.TryGetValue(id, out var entry)) { art = entry.Art; return entry.Ready; }
        entries[id] = new Entry();
        wanted.Enqueue(id);
        return false;
    }

    // Called from OfflineProbe.Update: loads at most one card.
    internal static void Tick()
    {
        if (broken || wanted.Count == 0) return;
        string id = wanted.Dequeue();
        if (!entries.TryGetValue(id, out var entry)) return;
        try { Load(id, entry); }
        catch (Exception error)
        {
            entry.Failed = true;
            if (error is MissingMethodException)
            {
                broken = true;
                log?.LogWarning("CARD_ART off for the session: " + error.Message);
            }
            else log?.LogInfo("CARD_ART " + id + " unavailable: " + error.Message);
        }
    }

    private static void Load(string id, Entry entry)
    {
        var loader = DefLoader.Get() ?? throw new InvalidOperationException("no DefLoader");
        entry.Def = loader.GetCardDef(id);
        CardDef def = entry.Def?.CardDef;
        if (def == null) { entry.Failed = true; return; }
        barPortrait = barPortrait ?? typeof(CardDef).GetMethod("GetDeckCardBarPortrait", new[] { typeof(TAG_PREMIUM) })
            ?? throw new MissingMethodException("CardDef", "GetDeckCardBarPortrait");
        portraitTexture = portraitTexture ?? typeof(CardDef).GetMethod("GetPortraitTexture", new[] { typeof(TAG_PREMIUM), typeof(bool), typeof(bool) })
            ?? throw new MissingMethodException("CardDef", "GetPortraitTexture");
        // The card's full art is the picture; the deck bar material says which part of it is painted. A minion's art is often
        // unpainted (white) outside its oval frame, and the bar's zoom (offset and scale, the same on both axes) frames the painted
        // part: its left and right edges, cut to the picture, are the strip's. Vertically the bar's own mesh decides, so the strip
        // keeps a fixed band a little above the middle (DefaultStrip), which suits every card seen so far.
        Texture portrait = Asset(portraitTexture.Invoke(def, new object[] { TAG_PREMIUM.NORMAL, true, false }), entry) as Texture;
        Material material = Asset(barPortrait.Invoke(def, new object[] { TAG_PREMIUM.NORMAL }), entry) as Material;
        Texture barTexture = material != null ? material.mainTexture : null;
        Rect barUv = material != null ? new Rect(material.mainTextureOffset, material.mainTextureScale) : Rect.zero;
        Texture texture = portrait ?? barTexture;
        Rect uv = DefaultStrip;
        float left = Mathf.Clamp01(barUv.xMin), right = Mathf.Clamp01(barUv.xMax);
        if (barUv.width > 0 && right - left > .2f) { uv.xMin = left; uv.xMax = right; }
        // Some arts are unpainted inside the bar's frame too: keep the widest stretch of the strip with no white columns.
        if (texture != null)
            try
            {
                bool[] blank = BlankColumns(texture);
                if (ArtStrip.Painted(blank, uv.xMin, uv.xMax, out float from, out float to)) { uv.xMin = from; uv.xMax = to; }
            }
            catch (Exception error) { log?.LogInfo("CARD_ART " + id + " white check skipped: " + error.Message); }
        log?.LogInfo("CARD_ART " + id + " portrait=" + Describe(portrait) + " bar=" + Describe(barTexture) + " barUv=" + Format(barUv)
            + " used=" + (texture == portrait ? "portrait" : "bar") + " uv=" + Format(uv));
        if (texture == null) { entry.Failed = true; return; }
        entry.Art = new Art { Texture = texture, Uv = uv };
        entry.Ready = true;
    }

    // Which columns of the art are mostly white in its middle rows, from a small copy read back from the GPU (the art itself is not
    // readable). The middle rows are symmetric about the centre, so a flipped read-back finds the same columns.
    internal const int Probe = 64;
    private static bool[] BlankColumns(Texture texture)
    {
        RenderTexture target = RenderTexture.GetTemporary(Probe, Probe), previous = RenderTexture.active;
        var copy = new Texture2D(Probe, Probe) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            Graphics.Blit(texture, target, Vector2.one, Vector2.zero);
            RenderTexture.active = target;
            copy.ReadPixels(new Rect(0, 0, Probe, Probe), 0, 0);
            copy.Apply();
            Color32[] pixels = copy.GetPixels32();
            var whites = new int[Probe];
            int rowFrom = Mathf.RoundToInt(Probe * .3f), rowTo = Mathf.RoundToInt(Probe * .7f);
            for (int y = rowFrom; y < rowTo; y++)
                for (int x = 0; x < Probe; x++)
                {
                    Color32 pixel = pixels[y * Probe + x];
                    if (pixel.r >= 230 && pixel.g >= 230 && pixel.b >= 230) whites[x]++;
                }
            return whites.Select(count => count >= (rowTo - rowFrom) / 4).ToArray();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(copy);
        }
    }

    // Where the game's deck tiles usually look: the full width, a band a little above the middle of the art.
    internal static readonly Rect DefaultStrip = new Rect(0f, .42f, 1f, .34f);

    private static Rect Clamp(Rect uv) => Rect.MinMaxRect(Mathf.Clamp01(uv.xMin), Mathf.Clamp01(uv.yMin), Mathf.Clamp01(uv.xMax), Mathf.Clamp01(uv.yMax));
    private static string Describe(Texture texture) => texture == null ? "none" : texture.name + ":" + texture.width + "x" + texture.height;
    private static string Format(Rect uv) => uv.x.ToString("0.###") + "," + uv.y.ToString("0.###") + "," + uv.width.ToString("0.###") + "," + uv.height.ToString("0.###");

    // An AssetHandle<T>'s asset; the handle is kept for disposal.
    private static object Asset(object handle, Entry entry)
    {
        if (handle == null) return null;
        if (handle is IDisposable disposable) entry.Handles.Add(disposable);
        var property = handle.GetType().GetProperty("Asset") ?? throw new MissingMethodException(handle.GetType().Name, "Asset");
        return property.GetValue(handle, null);
    }

    // The match is over: release every handle.
    internal static void Clear()
    {
        wanted.Clear();
        foreach (var entry in entries.Values)
        {
            foreach (var handle in entry.Handles) try { handle.Dispose(); } catch (Exception) { }
            try { entry.Def?.Dispose(); } catch (Exception) { }
        }
        entries.Clear();
    }

    // Where to sample the art for an area of `width` by `height` pixels: the strip's own crop, trimmed to the area's shape around
    // its middle so the art is never stretched.
    internal static Rect Fit(Art art, float width, float height)
    {
        Rect uv = art.Uv;
        if (art.Texture == null || width <= 0 || height <= 0) return uv;
        float source = Mathf.Abs(uv.width) * art.Texture.width / Mathf.Max(1e-4f, Mathf.Abs(uv.height) * art.Texture.height), target = width / height;
        if (target > source) { float keep = source / target; uv.y += uv.height * (1 - keep) / 2; uv.height *= keep; }
        else { float keep = target / source; uv.x += uv.width * (1 - keep) / 2; uv.width *= keep; }
        return Clamp(uv);
    }
}
