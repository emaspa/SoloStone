using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Options > Language: which client languages this copy can run, which one the next start uses, and the copy. Pure (no Unity,
// no client types), so the launcher tests run it.
// How the client picks its language on PC (HearthstoneLocalization.Initialize, read once at startup): the Battle.net launch
// option LOCALE, else client.config [Localization] Locale, else enUS. SoloStone's payload pins client.config (Locale = enUS) and
// the launcher refuses a changed one, so the choice goes in as the LOCALE launch option (OfflineProbe's GetLaunchOption guard)
// and is saved in the BepInEx config. Switching in place would need the native reset and download flow, which is mobile-only
// and online, so a change applies from the next start.
// A language needs its own files in the copy: Strings/<locale>/ for the game's text and, for every language but enUS (the base
// catalog), Data/Win/asset_manifest_<locale>.unity3d, the catalog of its localized asset bundles. Card names and text in the
// card database (Data/Win/dbf.unity3d) already hold every language. Battle.net installs a language's files when it is chosen as
// Hearthstone's text language; the SoloStone copy has the ones that were installed when it was made.
internal static class GameLanguage
{
    internal const string Default = "enUS";
    // The client's supported locales (Localization.SupportedLocales), in the order Options offers them.
    internal static readonly string[] Locales = { "enUS", "deDE", "esES", "esMX", "frFR", "itIT", "jaJP", "koKR", "plPL", "ptBR", "ruRU", "thTH", "zhCN", "zhTW" };
    // English names in plain ASCII: the plank's font has Latin glyphs in every client language, not always Cyrillic, Thai or CJK ones.
    private static readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal) {
        { "enUS", "English" }, { "deDE", "German" }, { "esES", "Spanish (ES)" }, { "esMX", "Spanish (MX)" }, { "frFR", "French" },
        { "itIT", "Italian" }, { "jaJP", "Japanese" }, { "koKR", "Korean" }, { "plPL", "Polish" }, { "ptBR", "Portuguese" },
        { "ruRU", "Russian" }, { "thTH", "Thai" }, { "zhCN", "Chinese (CN)" }, { "zhTW", "Chinese (TW)" }
    };

    internal static bool Known(string locale) => locale != null && names.ContainsKey(locale);
    internal static string Name(string locale) => Known(locale) ? names[locale] : locale;

    // Paths relative to the client folder, '/'-separated, that must exist for the client to run in this locale.
    internal static string[] RequiredFiles(string locale) => locale == Default
        ? new[] { "Strings/enUS/GLOBAL.txt" }
        : new[] { "Strings/" + locale + "/GLOBAL.txt", "Data/Win/asset_manifest_" + locale.ToLowerInvariant() + ".unity3d" };

    internal static List<string> Installed(Func<string, bool> exists) =>
        Locales.Where(locale => RequiredFiles(locale).All(path => { try { return exists(path); } catch { return false; } })).ToList();

    internal static List<string> Installed(string clientRoot) =>
        Installed(path => File.Exists(Path.Combine(clientRoot, path.Replace('/', Path.DirectorySeparatorChar))));

    // client.config's [Localization] Locale, as the client reads it (Section.Key, trimmed); null when absent or unreadable.
    internal static string ConfigLocale(IEnumerable<string> lines)
    {
        string section = null;
        foreach (string raw in lines ?? Enumerable.Empty<string>())
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[') { section = line.Trim('[', ']').Trim(); continue; }
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
            if (string.Equals(section, "Localization", StringComparison.OrdinalIgnoreCase) && string.Equals(key, "Locale", StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    // The client's own pick without SoloStone: client.config's locale when the client knows it, else enUS.
    internal static string ClientDefault(string configLocale) => Known(configLocale) ? configLocale : Default;

    // What the LOCALE launch option says at startup, or null to leave the client's own pick. The saved choice wins when this copy
    // has its files. Otherwise, if the client's own pick has no files here but another language has (a copy made from a
    // Battle.net install in another language), the first installed one. Anything else: null, the client as it was.
    internal static string StartLocale(string saved, string configLocale, IList<string> installed)
    {
        if (installed == null || installed.Count == 0) return null;
        if (Known(saved) && installed.Contains(saved)) return saved;
        return installed.Contains(ClientDefault(configLocale)) ? null : installed[0];
    }

    // BattleNet.GetLaunchOption's answer: the start locale for LOCALE when it is a client locale, null for every other key.
    internal const string LaunchKey = "LOCALE";
    internal static string LaunchValue(string key, string startLocale) => key == LaunchKey && Known(startLocale) ? startLocale : null;

    // The language this run started in.
    internal static string Running(string startLocale, string configLocale) => startLocale ?? ClientDefault(configLocale);

    // The language the next start uses when nothing changes until then.
    internal static string Next(string saved, string configLocale, IList<string> installed) =>
        StartLocale(saved, configLocale, installed) ?? ClientDefault(configLocale);

    // What one press offers: the installed language after `next`, or null when there is nothing else to switch to.
    internal static string Offer(string next, IList<string> installed)
    {
        if (installed == null || installed.Count < 2) return null;
        int at = installed.IndexOf(next);
        return installed[(at + 1) % installed.Count];
    }

    internal const string Id = "options.language", ObjectName = "SoloStoneLanguageOption";
    internal const string PopupId = "LOCAL_SOLOSTONE_LANGUAGE", ConfirmId = "language.switch", CancelId = "language.cancel", OkId = "language.ok";
    internal const string Header = "Language", SwitchLabel = "SWITCH", CancelLabel = "CANCEL";
    internal static string Label(string running, string next) => "Language: " + Name(next) + (next == running ? "" : " (restart)");

    internal static string SwitchBody(string running, string offer) =>
        "Play in " + Name(offer) + "? It applies the next time you start SoloStone; " + Name(running) + " stays until then. "
        + "Menus only SoloStone has stay in English.";

    internal static string OnlyBody(IList<string> installed) =>
        "This copy of the game has " + (installed == null || installed.Count == 0 ? "no language files SoloStone can check" : Name(installed[0]) + " only") + ". "
        + "To add a language, choose it as Hearthstone's text language in Battle.net and let it download, then uninstall SoloStone "
        + "and install it again (your saves stay). Options then offers every language the new copy has.";

    internal static string DialogButtonId(string popupId, string button) =>
        popupId != PopupId ? null : button == "confirm" ? ConfirmId : button == "cancel" ? CancelId : button == "okay" ? OkId : null;
    internal static bool IsDialogButtonId(string id) => id == ConfirmId || id == CancelId || id == OkId;
}
