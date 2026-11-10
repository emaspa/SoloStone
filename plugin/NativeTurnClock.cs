using System;
using BepInEx.Logging;
using UnityEngine;
using static MatchOverlay;

// The turn countdown, an IMGUI overlay during a match whose Turn Timer is on: m:ss left in the current turn, above the End
// Turn button. It reads the deadline NativeMatch enforces (NativeMatch.TurnTimeLeft) and keeps no clock of its own, so it
// reaches zero as the turn ends: the player's turn (45 s on turns 1-2, 75 s after) and the NPC's (75 s, held while the
// player answers a choice). It turns amber, then red, in the last 20 s, when the native rope burns, and is gone between
// turns and at zero. It takes no input and changes nothing; the match tracker's toggle key hides it too. An error disables
// it for the session with one warning.
internal static class NativeTurnClock
{
    private static ManualLogSource log;
    private static bool failed;
    private static long left = -1;
    private static bool npcTurn;
    private static string lastTrace;
    // The End Turn button on screen (GUI coordinates, empty when unknown). A sample counts only once the next one agrees: the
    // button flips at each turn change, and the clock keeps its place instead of following the turning mesh.
    private static NativeMatch placedFor;
    private static Rect button, lastSample;
    private static int buttonFrame = -100;
    internal const int RopeSeconds = 20, RedSeconds = 10;

    internal static void Initialize(ManualLogSource logger) => log = logger;

    // Called from OfflineProbe.Update, once per frame.
    internal static void Tick()
    {
        if (failed || MatchOverlay.Settings == null) return;
        try
        {
            left = -1;
            var match = ClientBridge.CurrentMatch;
            if (!MatchOverlay.Settings.TurnCountdown || match?.Game == null || !match.PlayerTurnTimerEnabled || !MatchOverlay.InMatch())
            { placedFor = null; lastTrace = null; return; }
            if (!ReferenceEquals(match, placedFor)) { placedFor = match; button = lastSample = Rect.zero; buttonFrame = -100; }
            if (Time.frameCount - buttonFrame >= 15) { buttonFrame = Time.frameCount; PlaceButton(); }
            if (!match.TurnTimeLeft(out long milliseconds, out npcTurn)) return;
            left = milliseconds;
            string trace = "turn=" + match.Game.Turn + " side=" + (npcTurn ? "npc" : "player") + " anchor=" + (button.width > 0 ? "end-turn-button" : "right-edge");
            if (trace != lastTrace) { lastTrace = trace; log.LogInfo("TURN_CLOCK " + trace + " seconds=" + Seconds(left)); }
        }
        catch (Exception error) { Fail(error); }
    }

    private static void PlaceButton()
    {
        var end = EndTurnButton.Get();
        GameObject mesh = end == null ? null : end.m_EndTurnButtonMesh;
        Rect sample = mesh == null ? Rect.zero : GuiRect(mesh.GetComponent<Renderer>(), mesh.layer);
        if (Same(sample, lastSample)) button = sample;
        lastSample = sample;
    }

    private static bool Same(Rect a, Rect b) =>
        Mathf.Abs(a.xMin - b.xMin) <= 1f && Mathf.Abs(a.xMax - b.xMax) <= 1f && Mathf.Abs(a.yMin - b.yMin) <= 1f && Mathf.Abs(a.yMax - b.yMax) <= 1f;

    // Whole seconds, rounded up like the native rope's own protocol value: 0:01 shows until the deadline itself.
    private static long Seconds(long milliseconds) => (milliseconds + 999) / 1000;

    private static void Fail(Exception error)
    {
        failed = true; left = -1;
        log?.LogWarning("TURN_CLOCK disabled for the session: " + error);
    }

    // Drawing: a pill in the card chips' style (NativeCardChips.Badge), like the attack counter beside each hero.
    private static readonly Color Calm = new Color(1f, 1f, 1f, .95f), Amber = new Color(1f, .74f, .3f), Red = new Color(1f, .33f, .28f);

    // Called from OfflineProbe.OnGUI. Repaint only: it takes no input.
    internal static void Draw()
    {
        if (failed || left <= 0 || MatchOverlay.Hidden || Event.current.type != EventType.Repaint) return;
        try
        {
            int height = Screen.height;
            long seconds = Seconds(left);
            string text = seconds / 60 + ":" + (seconds % 60).ToString("00");
            float pill = NativeCardChips.PillHeight, margin = height * NativeMatchTracker.MarginShare, x, y;
            if (button.width > 0)
            {
                x = button.center.x;
                y = button.yMin - margin - pill / 2f;
            }
            else
            {
                // No button on screen: the right edge, vertically centred, left of the deck tracker's column when it shows.
                float column = MatchOverlay.Settings.DeckTracker ? NativeMatchTracker.PanelWidth(height) + margin : 0f;
                x = Screen.width - margin - column - pill * 2f;
                y = height / 2f;
            }
            y = Mathf.Clamp(y, pill / 2f, Mathf.Max(pill / 2f, height - pill / 2f));
            // Sized for the widest reading, so the pill does not twitch as the digits change.
            NativeCardChips.Badge(text, seconds > RopeSeconds ? Calm : seconds > RedSeconds ? Amber : Red, x, y, NativeCardChips.Anchor.Centre, "0:00");
        }
        catch (Exception error) { Fail(error); }
    }
}
