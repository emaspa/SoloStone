using System;
using UnityEngine;

// The full card behind a hovered tracker tile: while the mouse is on a card tile in the deck tracker or the opponent tracker, the
// game's own card model (built as NativeCardHistory builds its cards) shows beside that panel at half the size of the game's
// enlarged card, level with the tile and kept on screen. Beside, not over: the panels are drawn after the game's cameras, so a
// model under a panel would be hidden by it. The model goes when nothing drew it for two frames (Sweep). An error turns only the
// preview off for the session.
internal static class NativeTrackerPreview
{
    // The game's enlarged card is about 42% of the screen's height (NativeCardHistory); the preview is half that.
    private const float HeightShare = .42f * .5f, WidthToHeight = .72f;
    private static bool failed;
    private static GameObject model;
    private static string modelId;
    private static float scale;
    private static int drawnFrame = -10;
    private static BepInEx.Logging.ManualLogSource log;

    internal static void Initialize(BepInEx.Logging.ManualLogSource logger) => log = logger;

    // Called every frame from NativeMatchTracker.Tick.
    internal static void Sweep()
    {
        if (model != null && Time.frameCount - drawnFrame > 2) Clear();
    }

    internal static void Clear()
    {
        if (model != null) UnityEngine.Object.Destroy(model);
        model = null; modelId = null; scale = 0f;
    }

    // `row` is the hovered tile and `panel` its panel, in GUI coordinates; `rightPanel` puts the card on the panel's left.
    internal static void Show(string cardId, Rect row, Rect panel, bool rightPanel)
    {
        if (failed || string.IsNullOrEmpty(cardId)) return;
        try
        {
            var hero = GameState.Get()?.GetFriendlySidePlayer()?.GetHero()?.GetCard()?.GetActor();
            if (hero == null) return;
            int layer = hero.gameObject.layer;
            var view = CameraUtils.FindFirstByLayerMask(1 << layer);
            if (view == null) return;
            if (cardId != modelId)
            {
                Clear();
                model = NativeCardHistory.Spawn(cardId, layer);
                if (model == null) return;
                modelId = cardId;
                log?.LogInfo("TRACKER_PREVIEW " + cardId);
            }
            drawnFrame = Time.frameCount;
            float height = Screen.height * HeightShare, width = height * WidthToHeight, gap = Screen.height * .01f;
            float x = rightPanel ? panel.xMin - gap - width / 2f : panel.xMax + gap + width / 2f;
            float y = Mathf.Clamp(row.center.y, height / 2f + gap, Screen.height - height / 2f - gap);
            float depth = view.WorldToScreenPoint(hero.transform.position).z * .8f;
            model.transform.rotation = hero.transform.rotation;
            model.transform.position = view.ScreenToWorldPoint(new Vector3(x, Screen.height - y, depth));
            if (scale <= 0f)
            {
                // Measured on the model as placed: the prefab's own scale to the wanted height on screen.
                Rect drawn = MatchOverlay.GuiRect(model.GetComponent<Actor>()?.GetMeshRenderer(), layer);
                if (drawn.height <= 0f) return;
                scale = model.transform.localScale.x * height / drawn.height;
            }
            model.transform.localScale = Vector3.one * scale;
        }
        catch (Exception error)
        {
            failed = true;
            try { Clear(); } catch (Exception) { }
            log?.LogWarning("TRACKER_PREVIEW off for the session: " + error);
        }
    }
}
