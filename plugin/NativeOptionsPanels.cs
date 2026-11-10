using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// SoloStone's own settings, behind two Options planks the way native Sound Options keeps its sliders behind one.
//
// Native Sound Options: OptionsMenu.OnSoundOptionsButtonReleased hides Options without its hide handler (Hide(false): the Options
// instance stays alive for Back), then instantiates the OptionsMenu_Sound prefab (OptionsMenu_Sound_phone on PC, by the client's own
// UsePhoneUI test) and shows it. SoundOptionsMenu.Awake hands the root to OverlayUI, wires its Back and Done planks and its sliders, and
// makes a full-screen input blocker; Show punches the root in. Back hides it and shows Options again, Done and a click outside only hide
// it, and Escape (BnetBar.HandleEscapeKey) does what Back does.
//
// Here: the main Options dialog gets two planks, "SoloStone Options" and "Tracker Options", clones of the Sound Options plank hung off
// the Options root (a pane would re-lay them, see NativeOptionsPace), in the right column's Other group under Disable Screen Shake. They
// take the first slot of OptionsPanelLayout.ButtonSlots that is inside the dialog and clear of every other row, in rendered bounds.
// Each opens a panel built from the same Sound Options prefab, loaded raw and copied under an inactive holder, so SoundOptionsMenu
// never wakes (no listeners, no SoundOptionsMenu.Get, no sliders): its component is removed and its sliders, checkboxes, Restore
// Defaults plank and inner section frames are switched off. Frame, header, Back and Done stay; the header reads the panel's title.
// Rows are copies of that prefab's own checkbox and plank, so they look like Sound Options' rows. The panel then behaves like Sound
// Options: Back and Escape return to Options, Done and a click outside close it. The controls keep their classes, settings and log
// lines (NativeTurnTimerOption, HandheldUi, NativeFpsCounter, NativeDeckInferenceOption, NativeOptionsPace, NativeLanguageOption,
// NativeStarterDecks, NativeMatchTrackerOptions); only where they live changed.
//
// Panels outlive the Options instance (OverlayUI keeps them) and are built once each. Layout runs on every open from rendered bounds
// (OptionsPanelLayout.Plan): until the open animation settles, then once more whenever a row comes or goes, and each settled layout
// logs one OPTIONS_PANEL line with every row's screen rect and any overlap. Every path fails closed: a failure logs one warning,
// hides what it could not place, and leaves native Options usable (a panel that cannot open brings Options back).
internal static class NativeOptionsPanels
{
    private const string SoundPrefab = "OptionsMenu_Sound.prefab:24e506e4b9be97b4fa45b0619a713517";
    private const string SoundPrefabPc = "OptionsMenu_Sound_phone.prefab:67dc77661d359d84ea46fbe496ee81d7";
    private static readonly FieldInfo SoundBack = AccessTools.Field(typeof(SoundOptionsMenu), "m_backButton");
    private static readonly FieldInfo SoundDone = AccessTools.Field(typeof(SoundOptionsMenu), "m_doneButton");
    private static readonly FieldInfo SoundReset = AccessTools.Field(typeof(SoundOptionsMenu), "m_resetDefaultsButton");
    private static readonly FieldInfo SoundMono = AccessTools.Field(typeof(SoundOptionsMenu), "m_monoSound");
    private static readonly FieldInfo SoundBackground = AccessTools.Field(typeof(SoundOptionsMenu), "m_backgroundSound");

    private sealed class Row
    {
        internal string Name, Id;
        internal Component Control;
        internal int Column;
        internal bool Check;
        internal Func<bool> Visible;
        internal Action Sync;
        internal Vector3 BaseScale;
    }

    private sealed class Panel
    {
        internal string Key;
        internal GameObject Root;
        internal Vector3 Normal;
        internal UIBButton Back, Done;
        internal PegUIElement Blocker;
        internal Transform Header, Frame, Bar;
        internal readonly List<Row> Rows = new List<Row>();
        internal bool Shown, Settled;
        internal int ShownFrame;
        internal string Visible;
        internal float Scale = 1;
    }

    // AnimationUtil.ShowWithPunch runs its coroutine on the root's first MonoBehaviour; SoundOptionsMenu, the prefab's only one, is
    // removed, so the panel root carries this instead.
    private sealed class Host : MonoBehaviour { }

    private static ManualLogSource log;
    private static UIBButton soloButton, trackerButton;
    private static bool buttonsPlaced, buttonsFailed;
    private static string buttonsSlot;
    private static GameObject soundPrefab, holder;
    private static bool loading;
    private static float loadStarted;
    private static string pendingKey;
    // A load that has not answered by then gives Options back (the press is dropped; the next press tries again).
    private const float LoadSeconds = 10f;
    private static Panel solo, tracker;
    private static bool paceFromPanel;

    internal static UIBButton SoloStoneButton => soloButton != null && soloButton.gameObject.activeInHierarchy ? soloButton : null;
    internal static UIBButton TrackerButton => trackerButton != null && trackerButton.gameObject.activeInHierarchy ? trackerButton : null;
    // The open panel's root, for Handheld UI's larger menu text (DeckLayout.TickMenus); null when none is on screen.
    internal static Transform ShownRoot => RootOf(solo) ?? RootOf(tracker);
    private static Transform RootOf(Panel panel) => panel != null && panel.Shown && panel.Root != null && panel.Root.activeInHierarchy ? panel.Root.transform : null;
    // A panel row's control is on screen (for the State helpers of the classes that own them).
    internal static bool OnScreen(Component control) => control != null && control.gameObject.activeInHierarchy && PanelOf(control)?.Shown == true;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake") ?? throw new MissingMethodException("OptionsMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(NativeOptionsPanels), nameof(AddButtons)));
        harmony.Patch(AccessTools.Method(typeof(BnetBar), "HandleEscapeKey") ?? throw new MissingMethodException("BnetBar", "HandleEscapeKey"),
            prefix: new HarmonyMethod(typeof(NativeOptionsPanels), nameof(BeforeEscape)));
    }

    // ---- The two planks in native Options (one pair per Options instance: Options is re-created after it closes).

    private static void AddButtons(OptionsMenu __instance)
    {
        soloButton = trackerButton = null; buttonsPlaced = buttonsFailed = false; buttonsSlot = null;
        try
        {
            UIBButton source = __instance.m_soundOptionsButton ?? __instance.m_miscellaneousButton;
            if (source == null) throw new InvalidOperationException("Options plank template missing");
            soloButton = Plank(__instance, source, OptionsPanelText.SoloStoneButtonObject, OptionsPanelText.SoloStoneButton, OptionsPanelText.SoloStoneKey);
            trackerButton = Plank(__instance, source, OptionsPanelText.TrackerButtonObject, OptionsPanelText.TrackerButton, OptionsPanelText.TrackerKey);
        }
        catch (Exception error)
        {
            HideButtons(); soloButton = trackerButton = null;
            log?.LogWarning("OPTIONS_PANELS buttons unavailable: " + error.Message);
        }
    }

    private static UIBButton Plank(OptionsMenu options, UIBButton source, string name, string label, string key)
    {
        GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, options.transform, true);
        clone.name = name;
        UIBButton plank = clone.GetComponent<UIBButton>();
        if (plank == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no plank"); }
        plank.ClearEventListeners();
        plank.SetText(label);
        plank.AddEventListener(UIEventType.RELEASE, _ => Open(key));
        clone.SetActive(false);
        return plank;
    }

    private static void HideButtons()
    {
        if (soloButton != null) soloButton.gameObject.SetActive(false);
        if (trackerButton != null) trackerButton.gameObject.SetActive(false);
    }

    // Called from OfflineProbe.Update, once per frame.
    internal static void Tick()
    {
        if (!buttonsPlaced && !buttonsFailed && soloButton != null && trackerButton != null) // Unity null: also true once Options is destroyed.
        {
            try { PlaceButtons(); }
            catch (Exception error) { buttonsFailed = true; HideButtons(); log?.LogWarning("OPTIONS_PANELS buttons placement: " + error.Message); }
        }
        if (loading && Time.realtimeSinceStartup - loadStarted > LoadSeconds)
        {
            loading = false;
            string key = pendingKey; pendingKey = null;
            Fail(key, new TimeoutException("Sound Options prefab load took over " + LoadSeconds + " s"));
        }
        TickPanel(solo);
        TickPanel(tracker);
    }

    // Once per Options instance, when Screen Shake has its rendered size (past the Show punch). Everything hangs off the Options root,
    // so the result holds through the punch and at any resolution.
    private static void PlaceButtons()
    {
        var options = OptionsMenu.Get();
        if (options == null || !options.IsShown()) return;
        CheckBox shake = options.m_screenShakeCheckbox;
        if (shake == null) throw new InvalidOperationException("Screen Shake checkbox missing");
        Camera view = CameraUtils.FindFirstByLayerMask(1 << shake.gameObject.layer);
        if (view == null || DeckLayout.ScreenRect(shake.gameObject, view).height < 12) return;
        Vector3 right = view.transform.right, up = view.transform.up;
        if (!Span(shake.gameObject, right, up, true, out PanelRect shakeRect) || !Span(soloButton.gameObject, right, up, false, out PanelRect plank))
            throw new InvalidOperationException("no rendered bounds");
        PanelRect above = options.m_spectatorOpenJoinCheckbox != null && Span(options.m_spectatorOpenJoinCheckbox.gameObject, right, up, true, out PanelRect spectator)
            ? spectator : new PanelRect(shakeRect.X, shakeRect.Y + shakeRect.Height * 1.5f, shakeRect.Width, shakeRect.Height);
        PanelRect? slot = options.m_miscellaneousButton != null && Span(options.m_miscellaneousButton.gameObject, right, up, false, out PanelRect misc) ? misc : (PanelRect?)null;

        var tried = new List<string>();
        KeyValuePair<string, PanelRect[]>? first = null;
        foreach (var candidate in OptionsPanelLayout.ButtonSlots(shakeRect, above, slot, plank.Width, plank.Height))
        {
            first = first ?? candidate;
            Put(candidate.Value, right, up);
            var clashes = ButtonClashes(options, view);
            if (clashes.Count == 0) { Placed(options, view, candidate.Key, "none"); return; }
            tried.Add(candidate.Key + ": " + string.Join("/", clashes));
        }
        // Nothing clear: keep the first slot (the settings stay reachable) and say what it overlaps.
        Put(first.Value.Value, right, up);
        Placed(options, view, first.Value.Key, string.Join("; ", tried));
    }

    private static void Put(PanelRect[] slots, Vector3 right, Vector3 up)
    {
        ProfileFormKit.Center(soloButton.gameObject, right, up, slots[0].CenterX, slots[0].CenterY);
        ProfileFormKit.Center(trackerButton.gameObject, right, up, slots[1].CenterX, slots[1].CenterY);
        soloButton.gameObject.SetActive(true); trackerButton.gameObject.SetActive(true);
    }

    private static void Placed(OptionsMenu options, Camera view, string slot, string overlaps)
    {
        buttonsPlaced = true; buttonsSlot = slot;
        string message = "OPTIONS_PANELS buttons slot=" + slot + " screen=" + Screen.width + "x" + Screen.height
            + " soloStone=" + Pixels(DeckLayout.ScreenRect(soloButton.gameObject, view)) + " tracker=" + Pixels(DeckLayout.ScreenRect(trackerButton.gameObject, view))
            + " screenShake=" + Pixels(DeckLayout.ScreenRect(options.m_screenShakeCheckbox.gameObject, view));
        if (options.m_middleBottomRightPane != null)
        {
            Rect group = DeckLayout.ScreenRect(options.m_middleBottomRightPane.gameObject, view);
            bool inside = Inside(group, DeckLayout.ScreenRect(soloButton.gameObject, view)) && Inside(group, DeckLayout.ScreenRect(trackerButton.gameObject, view));
            message += " otherGroup=" + Pixels(group) + " inOtherGroup=" + inside;
        }
        message += " overlaps=" + overlaps;
        if (overlaps == "none") log?.LogInfo(message); else log?.LogWarning(message);
    }

    private static bool Inside(Rect outer, Rect inner) => inner.xMin >= outer.xMin && inner.xMax <= outer.xMax && inner.yMin >= outer.yMin && inner.yMax <= outer.yMax;

    // Every native row and the other plank either plank overlaps (a tenth of each height is slack, as NativeFpsCounter.Overlaps), and
    // "outsideBody" when one leaves the dialog body.
    private static List<string> ButtonClashes(OptionsMenu options, Camera view)
    {
        var names = new List<string>();
        foreach (var button in new[] { soloButton, trackerButton })
        {
            string own = button == soloButton ? "soloStone" : "tracker";
            var clashes = NativeFpsCounter.Overlaps(options, button, Obstacles(options), out bool measured, soloButton, trackerButton);
            foreach (string clash in clashes) names.Add(own + ">" + clash);
            if (!measured) names.Add(own + ">notMeasured");
        }
        return names;
    }

    // What a plank in the Other group must clear, in release builds too: the native rows still shown, and the other plank. Placement and
    // the test-control state both check this list, so a slot the log calls clear is clear in state.optionsPanels too.
    internal static List<KeyValuePair<string, Component>> Obstacles(OptionsMenu options) => new List<KeyValuePair<string, Component>> {
        Pair("signature", options.m_signatureTextDisplayDropdown), Pair("frameRate", options.m_graphicsFps), Pair("fullscreen", options.m_fullScreenCheckbox),
        Pair("resolution", options.m_graphicsRes), Pair("quality", options.m_graphicsQuality), Pair("masterVolume", options.m_masterVolume),
        Pair("musicVolume", options.m_musicVolume), Pair("soundOptions", options.m_soundOptionsButton), Pair("screenShake", options.m_screenShakeCheckbox),
        Pair("versionLabel", VersionLabel?.GetValue(options) as Component),
        Pair("soloStoneOptions", soloButton), Pair("trackerOptions", trackerButton)
    };
    // UberText's assembly is not referenced here, so the field is read by name (as NativeOptionsPace).
    private static readonly FieldInfo VersionLabel = AccessTools.Field(typeof(OptionsMenu), "m_versionLabel");
    private static KeyValuePair<string, Component> Pair(string name, Component row) => new KeyValuePair<string, Component>(name, row);

    // ---- Opening, closing, returning.

    private static void Open(string key)
    {
        try
        {
            var options = OptionsMenu.Get();
            if (options != null && options.IsShown()) options.Hide(false); // No hide handler: that would destroy Options (as native Sound Options).
            Panel panel = Get(key);
            if (panel != null && panel.Root != null) { Show(panel); return; }
            if (soundPrefab != null) { panel = Build(key); Keep(key, panel); Show(panel); return; }
            pendingKey = key;
            if (loading) return;
            loading = true; loadStarted = Time.realtimeSinceStartup;
            NativeRewardUI.LoadRaw(UniversalInputManager.UsePhoneUI ? SoundPrefab : SoundPrefabPc, raw => {
                if (!loading) { if (raw != null) soundPrefab = raw; return; } // Timed out: keep the prefab for the next press.
                loading = false;
                string want = pendingKey; pendingKey = null;
                if (raw == null) { Fail(want, new InvalidOperationException("Sound Options prefab did not load")); return; }
                soundPrefab = raw;
                if (want != null) Open(want);
            });
        }
        catch (Exception error) { loading = false; pendingKey = null; Fail(key, error); }
    }

    private static Panel Get(string key) => key == OptionsPanelText.SoloStoneKey ? solo : tracker;
    private static void Keep(string key, Panel panel) { if (key == OptionsPanelText.SoloStoneKey) solo = panel; else tracker = panel; }

    // The panel goes before Options comes back, so the two never show together.
    private static void Fail(string key, Exception error)
    {
        log?.LogWarning("OPTIONS_PANEL " + (key ?? "?") + " unavailable: " + error);
        try { Panel panel = key == null ? null : Get(key); if (panel != null) Hide(panel); }
        catch (Exception hide) { log?.LogWarning("OPTIONS_PANEL " + key + " could not hide: " + hide.Message); }
        ShowOptions();
    }

    private static void ShowOptions()
    {
        try { var options = OptionsMenu.Get(); if (options != null && !options.IsShown()) options.Show(); }
        catch (Exception error) { log?.LogWarning("OPTIONS_PANEL could not show Options again: " + error.Message); }
    }

    private static void Show(Panel panel)
    {
        foreach (var row in panel.Rows)
        {
            try { if (row.Control != null) row.Sync?.Invoke(); }
            catch (Exception error) { log?.LogWarning("OPTIONS_PANEL " + panel.Key + " row " + row.Name + " sync: " + error.Message); }
        }
        panel.Root.transform.localScale = panel.Normal;
        UniversalInputManager.Get()?.SetGameDialogActive(true);
        panel.Root.SetActive(true);
        panel.Shown = true; panel.Settled = false; panel.Visible = null; panel.ShownFrame = Time.frameCount;
        try { Layout(panel); }
        catch (Exception error) { Hide(panel); Fail(panel.Key, error); return; }
        // The open punch is cosmetic: without it the panel just appears at its normal size.
        try { AnimationUtil.ShowWithPunch(panel.Root, .01f * panel.Normal, 1.1f * panel.Normal, panel.Normal, null, true); }
        catch (Exception error) { panel.Root.transform.localScale = panel.Normal; log?.LogWarning("OPTIONS_PANEL " + panel.Key + " open animation: " + error.Message); }
    }

    private static void Hide(Panel panel)
    {
        bool shown = panel.Shown;
        panel.Shown = false;
        // Show set the game's dialog flag; a root destroyed or switched off by something else still clears it.
        if (panel.Root == null || !panel.Root.activeSelf) { if (shown) UniversalInputManager.Get()?.SetGameDialogActive(false); return; }
        UniversalInputManager.Get()?.SetGameDialogActive(false);
        panel.Root.SetActive(false);
    }

    private static void GoBack(Panel panel) { Hide(panel); ShowOptions(); }

    // Done and a click outside: like native Sound Options, the panel goes and Options stays hidden (alive, for the next open).
    private static void Close(Panel panel) => Hide(panel);

    // HarmonyX runs every prefix, so one that already took the key (the Pace page's Escape brings this panel back) leaves it alone.
    private static bool BeforeEscape(ref bool __result, bool __runOriginal)
    {
        if (!__runOriginal) return false;
        try
        {
            foreach (var panel in new[] { solo, tracker })
                if (panel != null && panel.Shown && panel.Root != null && panel.Root.activeInHierarchy) { GoBack(panel); __result = true; return false; }
        }
        catch (Exception error) { log?.LogWarning("OPTIONS_PANEL escape: " + error.Message); }
        return true;
    }

    // Options > SoloStone Options > Pace: the Pace page replaces the panel, and its Back returns here (NativeProfileSelector.ReturnToOptions).
    internal static void LeaveForPace()
    {
        paceFromPanel = solo != null && solo.Shown;
        if (paceFromPanel) Hide(solo);
        else { var options = OptionsMenu.Get(); if (options != null && options.IsShown()) options.Hide(false); }
    }

    internal static void ReturnFromPace()
    {
        bool toPanel = paceFromPanel; paceFromPanel = false;
        if (toPanel && solo != null && solo.Root != null)
        {
            try { Show(solo); return; }
            catch (Exception error) { log?.LogWarning("OPTIONS_PANEL solostone after Pace: " + error.Message); }
        }
        ShowOptions();
    }

    // ---- Building a panel from the Sound Options prefab.

    private static Panel Build(string key)
    {
        if (holder == null)
        {
            holder = new GameObject("SoloStoneOptionsPanels");
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
        }
        // Under the inactive holder nothing wakes: SoundOptionsMenu goes before it can wire anything.
        GameObject root = UnityEngine.Object.Instantiate(soundPrefab, holder.transform);
        try
        {
            root.name = "SoloStoneOptionsPanel-" + key;
            var menu = root.GetComponent<SoundOptionsMenu>() ?? throw new InvalidOperationException("Sound Options prefab has no SoundOptionsMenu");
            var back = SoundBack?.GetValue(menu) as UIBButton;
            var done = SoundDone?.GetValue(menu) as UIBButton;
            var reset = SoundReset?.GetValue(menu) as UIBButton;
            var check = SoundMono?.GetValue(menu) as CheckBox;
            if (check == null) check = SoundBackground?.GetValue(menu) as CheckBox;
            if (back == null && done == null) throw new InvalidOperationException("Sound Options has no Back or Done");
            if (check == null || reset == null) throw new InvalidOperationException("Sound Options has no checkbox or plank to copy");
            UnityEngine.Object.DestroyImmediate(menu);
            root.AddComponent<Host>();
            foreach (var slider in root.GetComponentsInChildren<ScrollbarControl>(true)) slider.gameObject.SetActive(false);
            foreach (var box in root.GetComponentsInChildren<CheckBox>(true)) box.gameObject.SetActive(false);
            reset.gameObject.SetActive(false);
            foreach (Transform child in root.transform)
                if (child.name.StartsWith("OptionSection", StringComparison.Ordinal)) child.gameObject.SetActive(false);

            var panel = new Panel { Key = key, Root = root, Back = back, Done = done };
            panel.Header = root.transform.Find("Header");
            panel.Frame = root.transform.Find("OptionFrame") ?? root.transform;
            panel.Bar = TopChild(root.transform, (back ?? done).transform);
            Component title = FirstText(panel.Header);
            if (title != null) ProfileFormKit.SetText(title, OptionsPanelText.Title(key));
            else log?.LogWarning("OPTIONS_PANEL " + key + " header text not found; the title stays as shipped");
            if (back != null) back.AddEventListener(UIEventType.RELEASE, _ => GoBack(panel));
            if (done != null) done.AddEventListener(UIEventType.RELEASE, _ => { if (back == null) GoBack(panel); else Close(panel); });

            if (key == OptionsPanelText.SoloStoneKey)
            {
                AddCheck(panel, check, 0, "turnTimer", NativeTurnTimerOption.Id, "SoloStoneTurnTimerCheckbox", NativeTurnTimerOption.Label, NativeTurnTimerOption.Attach, NativeTurnTimerOption.Sync);
                AddCheck(panel, check, 0, "handheld", OptionsPanelText.HandheldId, "SoloStoneHandheldUiCheckbox", HandheldUi.Label, HandheldUi.Attach, HandheldUi.Sync);
                AddCheck(panel, check, 0, "fpsCounter", NativeFpsCounterText.Id, NativeFpsCounterText.ObjectName, NativeFpsCounterText.Label, NativeFpsCounter.Attach, NativeFpsCounter.Sync);
                AddCheck(panel, check, 0, "deckInference", NativeDeckInferenceText.Id, NativeDeckInferenceText.ObjectName, NativeDeckInferenceText.Label,
                    NativeDeckInferenceOption.Attach, NativeDeckInferenceOption.Sync);
                if (NativeOptionsPace.Available)
                    AddPlank(panel, reset, 1, "pace", NativeOptionsPaceText.Id, NativeOptionsPaceText.ObjectName, NativeOptionsPace.Attach, NativeOptionsPace.Sync, NativeOptionsPace.Wanted);
                if (NativeLanguageOption.Available)
                    AddPlank(panel, reset, 1, "language", GameLanguage.Id, GameLanguage.ObjectName, NativeLanguageOption.Attach, NativeLanguageOption.Sync, null);
                AddPlank(panel, reset, 1, "restoreStarters", NativeStarterText.OptionsId, NativeStarterText.OptionsObject, NativeStarterDecks.Attach, null, NativeStarterDecks.Wanted);
            }
            else
            {
                AddCheck(panel, check, 0, "countdown", OptionsPanelText.CountdownId, "SoloStoneCountdownCheckbox", OptionsPanelText.CountdownLabel,
                    box => NativeMatchTrackerOptions.Attach(0, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 0, "deckTracker", OptionsPanelText.DeckTrackerId, "SoloStoneDeckTrackerCheckbox", OptionsPanelText.DeckTrackerLabel,
                    box => NativeMatchTrackerOptions.Attach(1, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 0, "opponentTracker", OptionsPanelText.OpponentTrackerId, "SoloStoneOpponentTrackerCheckbox", OptionsPanelText.OpponentTrackerLabel,
                    box => NativeMatchTrackerOptions.Attach(6, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 0, "attackCounter", OptionsPanelText.AttackCounterId, "SoloStoneAttackCounterCheckbox", OptionsPanelText.AttackCounterLabel,
                    box => NativeMatchTrackerOptions.Attach(2, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 0, "secretHelper", OptionsPanelText.SecretHelperId, "SoloStoneSecretHelperCheckbox", OptionsPanelText.SecretHelperLabel,
                    box => NativeMatchTrackerOptions.Attach(8, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 0, "cardHistory", OptionsPanelText.CardHistoryId, "SoloStoneCardHistoryCheckbox", OptionsPanelText.CardHistoryLabel,
                    box => NativeMatchTrackerOptions.Attach(10, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 1, "cardArt", OptionsPanelText.CardArtId, "SoloStoneCardArtCheckbox", OptionsPanelText.CardArtLabel,
                    box => NativeMatchTrackerOptions.Attach(3, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 1, "deckStats", OptionsPanelText.DeckStatsId, "SoloStoneDeckStatsCheckbox", OptionsPanelText.DeckStatsLabel,
                    box => NativeMatchTrackerOptions.Attach(4, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 1, "mulliganGuide", OptionsPanelText.MulliganGuideId, "SoloStoneMulliganGuideCheckbox", OptionsPanelText.MulliganGuideLabel,
                    box => NativeMatchTrackerOptions.Attach(5, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 1, "cardChips", OptionsPanelText.CardChipsId, "SoloStoneCardChipsCheckbox", OptionsPanelText.CardChipsLabel,
                    box => NativeMatchTrackerOptions.Attach(7, box), NativeMatchTrackerOptions.Sync);
                AddCheck(panel, check, 1, "counterChips", OptionsPanelText.CounterChipsId, "SoloStoneCounterChipsCheckbox", OptionsPanelText.CounterChipsLabel,
                    box => NativeMatchTrackerOptions.Attach(9, box), NativeMatchTrackerOptions.Sync);
            }
            if (panel.Rows.Count == 0) throw new InvalidOperationException("no rows");

            root.SetActive(false);
            var overlay = OverlayUI.Get() ?? throw new InvalidOperationException("OverlayUI missing");
            overlay.AddGameObject(root); // Keeps the prefab's local transform, as native Sound Options.
            panel.Normal = root.transform.localScale;
            Camera view = CameraUtils.FindFirstByLayerMask(1 << root.layer) ?? throw new InvalidOperationException("no camera for the panel's layer");
            GameObject blocker = CameraUtils.CreateInputBlocker(view, "SoloStoneOptionsPanelInputBlocker", root.transform, root.transform, 10f);
            blocker.layer = root.layer;
            panel.Blocker = blocker.AddComponent<PegUIElement>();
            panel.Blocker.AddEventListener(UIEventType.RELEASE, _ => Close(panel));
            log?.LogInfo("OPTIONS_PANEL built=" + key + " from=" + soundPrefab.name + " back=" + (back != null) + " done=" + (done != null)
                + " title=" + (title != null) + " rows=" + string.Join(",", panel.Rows.Select(row => row.Name)));
            return panel;
        }
        catch
        {
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }

    private static Transform TopChild(Transform root, Transform node)
    {
        for (var at = node; at != null; at = at.parent) if (at.parent == root) return at;
        return node;
    }

    private static Component FirstText(Transform under)
    {
        if (under == null || ProfileFormKit.TextType == null) return null;
        foreach (Component text in under.GetComponentsInChildren(ProfileFormKit.TextType, true))
            if (ActiveUnder(text.transform, under)) return text;
        return null;
    }

    private static bool ActiveUnder(Transform node, Transform root)
    {
        for (var at = node; at != null && at != root; at = at.parent) if (!at.gameObject.activeSelf) return false;
        return true;
    }

    private static void AddCheck(Panel panel, CheckBox template, int column, string name, string id, string objectName, string label,
        Action<CheckBox> attach, Action sync, Func<bool> visible = null)
    {
        CheckBox box = null;
        try
        {
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            clone.name = objectName;
            box = clone.GetComponent<CheckBox>();
            if (box == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no checkbox"); }
            box.ClearEventListeners(); box.SetButtonText(label);
            clone.SetActive(true);
            attach(box);
            panel.Rows.Add(new Row { Name = name, Id = id, Control = box, Column = column, Check = true, Sync = sync, Visible = visible ?? (() => true),
                BaseScale = clone.transform.localScale });
        }
        catch (Exception error)
        {
            if (box != null) UnityEngine.Object.Destroy(box.gameObject);
            log?.LogWarning("OPTIONS_PANEL " + panel.Key + " row " + name + " unavailable: " + error.Message);
        }
    }

    private static void AddPlank(Panel panel, UIBButton template, int column, string name, string id, string objectName,
        Action<UIBButton> attach, Action sync, Func<bool> visible)
    {
        UIBButton plank = null;
        try
        {
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            clone.name = objectName;
            plank = clone.GetComponent<UIBButton>();
            if (plank == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no plank"); }
            plank.ClearEventListeners();
            clone.SetActive(true);
            attach(plank);
            panel.Rows.Add(new Row { Name = name, Id = id, Control = plank, Column = column, Check = false, Sync = sync, Visible = visible ?? (() => true),
                BaseScale = clone.transform.localScale });
        }
        catch (Exception error)
        {
            if (plank != null) UnityEngine.Object.Destroy(plank.gameObject);
            log?.LogWarning("OPTIONS_PANEL " + panel.Key + " row " + name + " unavailable: " + error.Message);
        }
    }

    // ---- Layout inside a panel.

    private static void TickPanel(Panel panel)
    {
        if (panel == null || !panel.Shown) return;
        if (panel.Root == null || !panel.Root.activeInHierarchy) { Hide(panel); return; } // Destroyed or hidden by something else.
        try
        {
            if (panel.Settled && Signature(panel) == panel.Visible) return;
            Layout(panel);
            bool still = Time.frameCount > panel.ShownFrame + 2
                && (panel.Root.transform.localScale - panel.Normal).sqrMagnitude <= panel.Normal.sqrMagnitude * 1e-6f;
            if (still) { panel.Settled = true; Report(panel); }
        }
        catch (Exception error)
        {
            log?.LogWarning("OPTIONS_PANEL " + panel.Key + " layout: " + error.Message);
            GoBack(panel);
        }
    }

    private static List<Row> Live(Panel panel) => panel.Rows.Where(row => row.Control != null).ToList();

    private static string Signature(Panel panel)
    {
        var parts = new List<string>();
        foreach (var row in Live(panel)) if (Wanted(row)) parts.Add(row.Name);
        return string.Join(",", parts);
    }

    private static bool Wanted(Row row)
    {
        try { return row.Visible == null || row.Visible(); }
        catch (Exception) { return false; }
    }

    // Columns of visible rows in the free area between header and button bar, from rendered bounds. Measured and moved in the same
    // frame along the camera's axes, so it holds at any point of the open animation.
    private static void Layout(Panel panel)
    {
        Camera view = CameraUtils.FindFirstByLayerMask(1 << panel.Root.layer) ?? throw new InvalidOperationException("no camera for the panel");
        Vector3 right = view.transform.right, up = view.transform.up;
        var rows = Live(panel);
        var shown = new List<List<Row>>();
        for (int column = 0; column < 2; column++)
        {
            var list = new List<Row>();
            foreach (var row in rows.Where(row => row.Column == column))
            {
                bool wanted = Wanted(row);
                if (row.Control.gameObject.activeSelf != wanted) row.Control.gameObject.SetActive(wanted);
                if (wanted) list.Add(row);
            }
            if (list.Count != 0) shown.Add(list);
        }
        panel.Visible = Signature(panel);
        if (shown.Count == 0) return;

        if (!Span(panel.Frame.gameObject, right, up, false, out PanelRect frame)) throw new InvalidOperationException("no frame bounds");
        float headerBottom = panel.Header != null && Span(panel.Header.gameObject, right, up, false, out PanelRect header) ? header.Y : frame.Top;
        float barTop = Span(panel.Bar.gameObject, right, up, false, out PanelRect bar) ? bar.Top : frame.Y;
        PanelRect region = OptionsPanelLayout.Region(frame, headerBottom, barTop);

        var sizes = new List<IList<PanelRow>>();
        foreach (var list in shown)
        {
            var column = new List<PanelRow>();
            foreach (var row in list)
            {
                row.Control.transform.localScale = row.BaseScale;
                ProfileFormKit.UpdateNow(Label(row));
                if (!Span(row.Control.gameObject, right, up, true, out PanelRect own)) throw new InvalidOperationException("no bounds for " + row.Name);
                column.Add(new PanelRow(own.Width, own.Height, row.Check));
            }
            sizes.Add(column);
        }
        PanelPlan plan = OptionsPanelLayout.Plan(region, sizes);
        panel.Scale = plan.Scale;
        for (int c = 0; c < shown.Count; c++)
            for (int i = 0; i < shown[c].Count; i++)
            {
                Row row = shown[c][i];
                PanelRect target = plan.Rows[c][i];
                row.Control.transform.localScale = row.BaseScale * plan.Scale;
                if (!Span(row.Control.gameObject, right, up, true, out PanelRect now)) throw new InvalidOperationException("no bounds for " + row.Name);
                row.Control.transform.position += right * (target.X - now.X) + up * (target.CenterY - now.CenterY);
            }
    }

    private static Component Label(Row row) => row.Control is CheckBox box ? ProfileFormKit.Label(box) : row.Control is UIBButton plank ? ProfileFormKit.Label(plank) : null;

    // One line per settled layout: every visible row's screen rect (pixels, top-left origin) and what it overlaps.
    private static void Report(Panel panel)
    {
        Camera view = CameraUtils.FindFirstByLayerMask(1 << panel.Root.layer);
        if (view == null) return;
        var parts = new List<string>(); var overlaps = new List<string>();
        foreach (var row in Live(panel).Where(row => row.Control.gameObject.activeInHierarchy))
        {
            parts.Add(row.Name + "=" + Pixels(DeckLayout.ScreenRect(row.Control.gameObject, view)));
            foreach (string clash in RowClashes(panel, row.Control, view, out bool _)) overlaps.Add(row.Name + ">" + clash);
        }
        string message = "OPTIONS_PANEL open=" + panel.Key + " screen=" + Screen.width + "x" + Screen.height + " scale=" + panel.Scale.ToString("0.###")
            + " frame=" + Pixels(DeckLayout.ScreenRect(panel.Frame.gameObject, view)) + " " + string.Join(" ", parts)
            + " overlaps=" + (overlaps.Count == 0 ? "none" : string.Join(",", overlaps));
        if (overlaps.Count == 0) log?.LogInfo(message); else log?.LogWarning(message);
    }

    // The other visible rows, the header and the Back/Done bar a panel row overlaps (a tenth of each height is slack: rows' bounds
    // include their glow), and "outsideFrame" when it leaves the frame.
    private static List<string> RowClashes(Panel panel, Component control, Camera view, out bool measured)
    {
        var names = new List<string>(); measured = false;
        Rect mine = DeckLayout.ScreenRect(control.gameObject, view);
        if (mine.height < 4) return names;
        measured = true;
        var others = Live(panel).Where(row => row.Control != control && row.Control.gameObject.activeInHierarchy)
            .Select(row => new KeyValuePair<string, GameObject>(row.Name, row.Control.gameObject)).ToList();
        if (panel.Header != null) others.Add(new KeyValuePair<string, GameObject>("header", panel.Header.gameObject));
        if (panel.Bar != null) others.Add(new KeyValuePair<string, GameObject>("buttons", panel.Bar.gameObject));
        foreach (var other in others)
        {
            Rect rect = DeckLayout.ScreenRect(other.Value, view);
            if (rect.width > 0 && PanelRect.Overlap(Core(mine), Core(rect))) names.Add(other.Key);
        }
        if (!Inside(DeckLayout.ScreenRect(panel.Frame.gameObject, view), mine)) names.Add("outsideFrame");
        return names;
    }

    private static PanelRect Core(Rect rect) => new PanelRect(rect.x, rect.y + rect.height * .1f, rect.width, rect.height * .8f);

    // ---- Shared helpers.

    // Rendered extent of root along the camera's right/up axes (ProfileFormKit.Span), as a rect.
    private static bool Span(GameObject root, Vector3 right, Vector3 up, bool text, out PanelRect rect)
    {
        rect = default;
        if (!ProfileFormKit.Span(root, right, out float x0, out float x1, includeText: text) || !ProfileFormKit.Span(root, up, out float y0, out float y1, includeText: text)) return false;
        rect = new PanelRect(x0, y0, x1 - x0, y1 - y0);
        return true;
    }

    // Screen pixels, top-left origin like a screenshot.
    private static string Pixels(Rect rect) =>
        "[" + Mathf.RoundToInt(rect.x) + "," + Mathf.RoundToInt(Screen.height - rect.yMax) + "," + Mathf.RoundToInt(rect.width) + "," + Mathf.RoundToInt(rect.height) + "]";

    private static Panel PanelOf(Component control)
    {
        foreach (var panel in new[] { solo, tracker })
            if (panel != null && panel.Root != null && control.transform.IsChildOf(panel.Root.transform)) return panel;
        return null;
    }

    // Test control and the UI tree: each panel's Back, Done and outside click by id.
    internal static IEnumerable<KeyValuePair<PegUIElement, string>> Controls
    {
        get
        {
            foreach (var panel in new[] { solo, tracker })
            {
                if (panel == null || panel.Root == null) continue;
                if (panel.Back != null) yield return new KeyValuePair<PegUIElement, string>(panel.Back, OptionsPanelText.BackId(panel.Key));
                if (panel.Done != null) yield return new KeyValuePair<PegUIElement, string>(panel.Done, OptionsPanelText.DoneId(panel.Key));
                if (panel.Blocker != null) yield return new KeyValuePair<PegUIElement, string>(panel.Blocker, OptionsPanelText.DismissId(panel.Key));
            }
        }
    }

    // Test control: a panel checkbox's own state by its id (false when there is none).
    internal static bool CheckState(string id, out bool on)
    {
        on = false;
        foreach (var panel in new[] { solo, tracker })
        {
            if (panel == null) continue;
            foreach (var row in panel.Rows)
                if (row.Check && row.Id == id && row.Control is CheckBox box && box != null) { on = box.IsChecked(); return true; }
        }
        return false;
    }

#if !SOLOSTONE_RELEASE
    // Test control (state.optionsPanels): both Options planks and both panels, every rect normalized 1280x720 with a top-left origin
    // like every ui rect, and what each overlaps (Options rows for the planks, the other rows, header and buttons for a panel row).
    internal static JsonNode State()
    {
        var node = new JsonNode { { "slot", buttonsSlot }, { "placed", buttonsPlaced }, { "failed", buttonsFailed }, { "prefabLoaded", soundPrefab != null }, { "loading", loading } };
        var options = OptionsMenu.Get();
        node["buttons"] = new JsonNode { { "soloStone", ButtonState(options, soloButton) }, { "tracker", ButtonState(options, trackerButton) } };
        node["panels"] = new JsonNode { { OptionsPanelText.SoloStoneKey, PanelState(solo) }, { OptionsPanelText.TrackerKey, PanelState(tracker) } };
        return node;
    }

    private static JsonNode ButtonState(OptionsMenu options, UIBButton button)
    {
        var node = new JsonNode { { "shown", button != null && button.gameObject.activeInHierarchy } };
        if (button == null || options == null || !button.gameObject.activeInHierarchy) return node;
        try
        {
            Camera view = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
            if (view == null) return node;
            node["rect"] = Normalized(DeckLayout.ScreenRect(button.gameObject, view));
            node["overlaps"] = TestControlProtocol.List(NativeFpsCounter.Overlaps(options, button, Obstacles(options), out bool measured, soloButton, trackerButton)
                .Cast<object>());
            node["measured"] = measured;
        }
        catch (Exception error) { node["error"] = error.Message; }
        return node;
    }

    private static JsonNode PanelState(Panel panel)
    {
        var node = new JsonNode { { "built", panel != null && panel.Root != null }, { "shown", panel != null && panel.Shown }, { "settled", panel != null && panel.Settled } };
        if (panel == null || panel.Root == null) return node;
        node["scale"] = Math.Round((double)panel.Scale, 3);
        var rows = new JsonList();
        foreach (var row in Live(panel)) rows.Add(RowState(row.Control, row.Name, row.Id));
        node["rows"] = rows;
        return node;
    }

    // One panel row: shown, its rect and what it overlaps (see RowClashes); also used by the State of the class that owns it.
    internal static JsonNode RowState(Component control, string name = null, string id = null)
    {
        var node = new JsonNode { { "shown", OnScreen(control) } };
        if (name != null) node["name"] = name;
        if (id != null) node["id"] = id;
        if (control != null && control is CheckBox box) node["checked"] = box.IsChecked();
        Panel panel = control == null ? null : PanelOf(control);
        if (!OnScreen(control) || panel == null) return node;
        try
        {
            Camera view = CameraUtils.FindFirstByLayerMask(1 << panel.Root.layer);
            if (view == null) return node;
            node["rect"] = Normalized(DeckLayout.ScreenRect(control.gameObject, view));
            node["overlaps"] = TestControlProtocol.List(RowClashes(panel, control, view, out bool measured).Cast<object>());
            node["measured"] = measured;
        }
        catch (Exception error) { node["error"] = error.Message; }
        return node;
    }

    private static JsonList Normalized(Rect rect) => TestControlProtocol.List(new object[] {
        Math.Round(rect.x * 1280.0 / Screen.width, 1), Math.Round((Screen.height - rect.yMax) * 720.0 / Screen.height, 1),
        Math.Round(rect.width * 1280.0 / Screen.width, 1), Math.Round(rect.height * 720.0 / Screen.height, 1) });
#endif
}
