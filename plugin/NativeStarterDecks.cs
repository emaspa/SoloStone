using System;
using BepInEx.Logging;
using UnityEngine;

// #57 hidden starter decks (owner 2026-10-28). Deleting a Core starter loaner in the native tray hides it for this save
// (NativeDeckEditor). This adds the way back as a rare fallback in Options > SoloStone Options: a plank labelled "Restore Starter
// Decks" (NativeOptionsPanels builds the row and hands it to Attach), shown only while the save hides at least one starter (re-checked
// each time the panel lays out). It opens one native confirm; RESTORE clears the hidden list, and the starters return with the next
// native deck-list rebuild (the next start). Every path fails closed: nothing throws into Options.
internal static class NativeStarterDecks
{
    private static ManualLogSource log;
    private static UIBButton button;

    internal static UIBButton OptionsButton => NativeOptionsPanels.OnScreen(button) ? button : null;

    internal static void Initialize(ManualLogSource logger) { log = logger; }

    internal static bool ButtonWanted(LocalProfile profile, bool inMatch) => profile != null && !inMatch && profile.HiddenLoaners.Count != 0;

    internal static bool Wanted() =>
        ButtonWanted(NativeUIProbe.RewardProfile, SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY);

    // The panel's row, built once; it stays for the session. The panel shows it while Wanted.
    internal static void Attach(UIBButton plank)
    {
        plank.SetText(NativeStarterText.OptionsLabel);
        plank.AddEventListener(UIEventType.RELEASE, _ => ConfirmRestore());
        button = plank;
    }

    private static void ConfirmRestore()
    {
        try
        {
            DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
                m_id = NativeStarterText.RestorePopupId, m_headerText = NativeStarterText.RestoreHeader, m_text = NativeStarterText.RestoreBody,
                m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_confirmText = NativeStarterText.RestoreLabel, m_cancelText = NativeStrings.Get("GLOBAL_CANCEL", NativeStarterText.CancelLabel).ToUpperInvariant(), m_keyboardEscIsCancel = true,
                m_responseCallback = (response, data) => { if (response == AlertPopup.Response.CONFIRM) Restore(); }
            });
        }
        catch (Exception error) { log?.LogWarning("STARTER_DECKS restore confirm unavailable: " + error.Message); }
    }

    private static void Restore()
    {
        try
        {
            var profile = NativeUIProbe.RewardProfile;
            if (profile == null) return;
            bool changed = profile.RestoreLoaners();
            log?.LogInfo("STARTER_DECKS restored=" + changed); // Nothing is hidden now: the panel drops the row on its next frame.
        }
        catch (Exception error) { log?.LogWarning("STARTER_DECKS restore not saved: " + error.Message); }
    }
}
