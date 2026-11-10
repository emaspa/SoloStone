using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using MiniJSON;
using UnityEngine;

// Options > SoloStone Options > plank "Language: <name>" (NativeOptionsPanels builds the row and hands it to Attach) for the game's
// language. GameLanguage holds the rules: the client reads its language once at startup, so a choice is saved in the BepInEx config
// ([Display] Language) and handed to the next start as the LOCALE launch option (OfflineProbe.LaunchOption); client.config stays as
// shipped. The plank offers only the languages whose files are in this copy. A press opens one native popup: with one language it says
// how to add another, otherwise it offers the next installed language and SWITCH saves it; the plank then reads
// "Language: <name> (restart)". Every path fails closed: nothing throws into Options or startup, and a failure leaves the client's own
// language choice in place.
internal static class NativeLanguageOption
{
    private static LanguageSettings settings;
    private static ManualLogSource log;
    private static List<string> installed = new List<string>();
    private static string configLocale, startLocale, askedLocale;
    private static bool asked;
    private static UIBButton button;

    internal static UIBButton OptionsButton => NativeOptionsPanels.OnScreen(button) ? button : null;
    // The settings loaded: the panel has a Language row.
    internal static bool Available => settings != null;
    // The language this run started in: what the client was told when it asked, else what it will be told.
    internal static string Running => GameLanguage.Running(asked ? askedLocale : startLocale, configLocale);

    internal static void Initialize(ManualLogSource logger, ConfigFile file)
    {
        log = logger;
        try
        {
            settings = new LanguageSettings(file);
            string root = BepInEx.Paths.GameRootPath;
            installed = GameLanguage.Installed(root);
            try { configLocale = GameLanguage.ConfigLocale(File.ReadAllLines(Path.Combine(root, "client.config"))); }
            catch (Exception) { configLocale = null; }
            startLocale = GameLanguage.StartLocale(settings.Locale, configLocale, installed);
            log.LogInfo("GAME_LANGUAGE installed=" + string.Join(",", installed) + " clientConfig=" + (configLocale ?? "-")
                + " saved=" + (settings.Locale.Length == 0 ? "-" : settings.Locale) + " start=" + GameLanguage.Running(startLocale, configLocale));
            if (settings.Locale.Length != 0 && startLocale != settings.Locale && GameLanguage.ClientDefault(configLocale) != settings.Locale)
                log.LogWarning("GAME_LANGUAGE saved " + settings.Locale + " has no files in this copy; starting in " + GameLanguage.Running(startLocale, configLocale));
        }
        catch (Exception error)
        {
            settings = null; startLocale = null; installed = new List<string>();
            log.LogWarning("GAME_LANGUAGE unavailable, the client keeps its own language: " + error.Message);
        }
    }

    // BattleNet.GetLaunchOption, through OfflineProbe's guard: only LOCALE has a value, and only a supported locale this copy has
    // files for. Everything else (login tokens included) stays null. HearthstoneLocalization.Initialize asks once, at startup.
    internal static string LaunchOption(string key)
    {
        if (key != GameLanguage.LaunchKey) return null;
        string value = GameLanguage.LaunchValue(key, startLocale);
        if (!asked)
        {
            asked = true; askedLocale = value;
            log?.LogInfo("GAME_LANGUAGE client asked for LOCALE: " + (value ?? "none, client.config decides"));
        }
        return value;
    }

    // The panel's row, built once; it stays for the session.
    internal static void Attach(UIBButton plank)
    {
        if (settings == null) throw new InvalidOperationException("language settings unavailable");
        plank.AddEventListener(UIEventType.RELEASE, _ => Press());
        button = plank;
        Sync();
    }

    private static string Next() => GameLanguage.Next(settings.Locale, configLocale, installed);

    internal static void Sync()
    {
        try { if (button != null && settings != null) button.SetText(GameLanguage.Label(Running, Next())); }
        catch (Exception error) { log?.LogWarning("GAME_LANGUAGE_OPTION sync: " + error.Message); }
    }

    private static void Press()
    {
        try
        {
            if (settings == null) return;
            string running = Running, offer = GameLanguage.Offer(Next(), installed);
            var info = new AlertPopup.PopupInfo {
                m_id = GameLanguage.PopupId, m_headerText = GameLanguage.Header, m_showAlertIcon = false, m_keyboardEscIsCancel = true
            };
            if (offer == null)
            {
                info.m_text = GameLanguage.OnlyBody(installed);
                info.m_responseDisplay = AlertPopup.ResponseDisplay.OK;
                info.m_okText = GameStrings.Get("GLOBAL_OKAY");
            }
            else
            {
                info.m_text = GameLanguage.SwitchBody(running, offer);
                info.m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL;
                info.m_confirmText = GameLanguage.SwitchLabel; info.m_cancelText = NativeStrings.Get("GLOBAL_CANCEL", GameLanguage.CancelLabel).ToUpperInvariant();
                info.m_responseCallback = (response, data) => { if (response == AlertPopup.Response.CONFIRM) Choose(offer); };
            }
            DialogManager.Get()?.ShowUniquePopup(info);
        }
        catch (Exception error) { log?.LogWarning("GAME_LANGUAGE_OPTION popup unavailable: " + error.Message); }
    }

    private static void Choose(string locale)
    {
        try
        {
            settings.Set(locale);
            log?.LogInfo("GAME_LANGUAGE_OPTION next=" + locale + " running=" + Running + " applies=next-start");
        }
        catch (Exception error) { log?.LogWarning("GAME_LANGUAGE_OPTION not saved: " + error.Message); }
        Sync();
    }

#if !SOLOSTONE_RELEASE
    // Test control (state.language): what this run started in, what the next start uses, and the plank in its panel.
    internal static JsonNode State() => new JsonNode {
        { "installed", TestControlProtocol.List(installed.Cast<object>()) }, { "clientConfig", configLocale }, { "saved", settings?.Locale },
        { "running", Running }, { "next", settings == null ? null : Next() }, { "asked", asked },
        { "shown", OptionsButton != null }, { "plank", NativeOptionsPanels.RowState(button) }
    };
#endif
}
