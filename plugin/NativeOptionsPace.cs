using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Options > SoloStone Options > plank "Pace: <tier>" (NativeOptionsPanels builds the row and hands it to Attach) that opens the Pace
// page (NativeProfileMenu.BuildPace) in the panel's place; the page's Back returns to the panel (NativeOptionsPanels.ReturnFromPace),
// the way native Sound Options returns to Options. Shown only while Wanted (NativeOptionsPaceText.Wanted). Every path fails closed:
// nothing throws into Options.
internal static class NativeOptionsPace
{
    private static ManualLogSource log;
    private static UIBButton button;

    internal static UIBButton OptionsButton => NativeOptionsPanels.OnScreen(button) ? button : null;
    // NativeProfileSelector initializes Pace only for the owner's own client (no snapshot, lane 0).
    internal static bool Available => log != null;

    internal static void Initialize(ManualLogSource logger) { log = logger; }

    internal static bool Wanted() => NativeOptionsPaceText.Wanted(LocalLadder.Profile != null, NativeProfileSelector.InMatch, NativeAchievements.Active);

    // The panel's row, built once; it stays for the session.
    internal static void Attach(UIBButton plank)
    {
        plank.AddEventListener(UIEventType.RELEASE, _ => Open());
        button = plank;
        Sync();
    }

    internal static void Sync()
    {
        try { if (button != null && Wanted()) button.SetText(NativeOptionsPaceText.Label(LocalLadder.Profile.GrindTier.Name)); }
        catch (Exception error) { log?.LogWarning("OPTIONS_PACE sync: " + error.Message); }
    }

#if !SOLOSTONE_RELEASE
    // Every native Options row and both Options planks, by the name test control reports (NativeOptionsPanels.State). Entries may be null.
    internal static List<KeyValuePair<string, Component>> Rows(OptionsMenu options) => new List<KeyValuePair<string, Component>> {
        Row("signature", options.m_signatureTextDisplayDropdown), Row("frameRate", options.m_graphicsFps), Row("fullscreen", options.m_fullScreenCheckbox),
        Row("resolution", options.m_graphicsRes), Row("quality", options.m_graphicsQuality), Row("graphicsGroup", options.m_graphicsGroup?.transform),
        Row("masterVolume", options.m_masterVolume), Row("musicVolume", options.m_musicVolume), Row("soundGroup", options.m_soundGroup?.transform),
        Row("soundOptions", options.m_soundOptionsButton), Row("otherPane", options.m_middleBottomRightPane), Row("versionLabel", VersionLabel.GetValue(options) as Component),
        Row("screenShake", options.m_screenShakeCheckbox),
        Row("soloStoneOptions", NativeOptionsPanels.SoloStoneButton), Row("trackerOptions", NativeOptionsPanels.TrackerButton)
    };
    // UberText's assembly is not referenced here, so the field is read by name.
    private static readonly System.Reflection.FieldInfo VersionLabel = AccessTools.Field(typeof(OptionsMenu), "m_versionLabel");
    private static KeyValuePair<string, Component> Row(string name, Component row) => new KeyValuePair<string, Component>(name, row);

    // Test control (save-catalog.paceOption): whether the plank is wanted and on screen, its rect and every panel row it overlaps.
    internal static JsonNode State()
    {
        var node = NativeOptionsPanels.RowState(button);
        node["wanted"] = Available && Wanted();
        return node;
    }
#endif

    private static void Open()
    {
        try
        {
            Sync();
            if (!Wanted()) return;
            NativeOptionsPanels.LeaveForPace();
            NativeProfileSelector.OpenPace();
        }
        catch (Exception error) { log?.LogWarning("OPTIONS_PACE open: " + error.Message); }
    }
}
