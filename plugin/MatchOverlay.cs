using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

// What the in-match IMGUI overlays share (NativeMatchTracker's deck tracker and attack counters, NativeTurnClock's turn
// countdown): their settings, the toggle key that hides them all, when a match is on screen, and the drawing helpers.
internal static class MatchOverlay
{
    private static ManualLogSource log;
    private static bool toggleFailed;
    internal static MatchTrackerSettings Settings { get; private set; }
    // Flipped by the toggle key. It outlasts the match: hidden overlays stay hidden in the next one.
    internal static bool Hidden { get; private set; }

    internal static void Initialize(ManualLogSource logger, ConfigFile file) { log = logger; Settings = new MatchTrackerSettings(file); }

    // Called from OfflineProbe.Update before the overlays tick. Reads the key only in a match with an overlay on.
    internal static void Tick()
    {
        if (toggleFailed || Settings == null) return;
        try
        {
            if (!Settings.DeckTracker && !Settings.AttackCounter && !Settings.TurnCountdown) return;
            if (ClientBridge.CurrentMatch?.Game == null || !InMatch()) return;
            if (InputCollection.GetKeyUp(Settings.Toggle)) Hidden = !Hidden;
        }
        catch (Exception error) { toggleFailed = true; log?.LogWarning("MATCH_OVERLAY toggle key disabled for the session: " + error); }
    }

    internal static bool InMatch()
    {
        var scenes = SceneMgr.Get();
        return scenes != null && scenes.GetMode() == SceneMgr.Mode.GAMEPLAY && GameState.Get() != null && GameState.Get().IsGameOver() == false;
    }

    // A renderer's on-screen bounds in GUI coordinates (top-left origin), through the camera that draws its layer.
    // Empty when it is hidden or no camera draws it.
    internal static Rect GuiRect(Renderer renderer, int layer)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return Rect.zero;
        Camera view = CameraUtils.FindFirstByLayerMask(1 << layer);
        if (view == null) return Rect.zero;
        Rect screen = DeckLayout.Project(renderer.bounds, view);
        return screen.width <= 0 ? Rect.zero : new Rect(screen.xMin, Screen.height - screen.yMax, screen.width, screen.height);
    }

    internal enum Align { Left, Center, Right }
    private static readonly GUIContent content = new GUIContent();

    internal static Vector2 Measure(string text, GUIStyle style) { content.text = text; return style.CalcSize(content); }

    // Measured and placed here, vertically centred: GUIStyle.alignment would need another Unity module.
    internal static void Text(Rect area, string text, GUIStyle style, Align align)
    {
        Vector2 size = Measure(text, style);
        float width = Mathf.Min(size.x, area.width);
        float x = align == Align.Left ? area.x : align == Align.Right ? area.xMax - width : area.x + (area.width - width) / 2f;
        GUI.Label(new Rect(x, area.y + (area.height - size.y) / 2f, width, size.y), content, style);
    }

    // A one-line label style from the skin's label: no wrap, no padding, clipped to its area.
    internal static GUIStyle Label(int fontSize, Color color)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(8, fontSize), wordWrap = false, clipping = TextClipping.Clip };
        style.padding = new RectOffset(0, 0, 0, 0);
        style.normal.textColor = color;
        return style;
    }

    internal static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color); texture.Apply();
        return texture;
    }
}
