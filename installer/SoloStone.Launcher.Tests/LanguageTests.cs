using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Options > Language (plugin/GameLanguage.cs): which languages a copy can run, what the next start uses, and the copy the player reads.
static partial class Program
{
    static void LanguageTests(string temp) {
        var enOnly=new HashSet<string>{"Strings/enUS/GLOBAL.txt"};
        Check(GameLanguage.Installed(enOnly.Contains).SequenceEqual(new[]{"enUS"}),"an English-only copy offers English");
        var withGerman=new HashSet<string>(enOnly){"Strings/deDE/GLOBAL.txt","Data/Win/asset_manifest_dede.unity3d"};
        Check(GameLanguage.Installed(withGerman.Contains).SequenceEqual(new[]{"enUS","deDE"}),"a language with its strings and asset catalog is installed");
        Check(GameLanguage.Installed(new HashSet<string>(enOnly){"Strings/frFR/GLOBAL.txt"}.Contains).SequenceEqual(new[]{"enUS"}),"strings without the asset catalog are not enough");
        Check(GameLanguage.Installed(new HashSet<string>(enOnly){"Data/Win/asset_manifest_frfr.unity3d"}.Contains).SequenceEqual(new[]{"enUS"}),"an asset catalog without strings is not enough");
        Check(GameLanguage.Installed(path=>throw new IOException("denied")).Count==0,"a file check that throws counts as missing");
        Check(GameLanguage.Locales.All(GameLanguage.Known) && GameLanguage.Locales.Distinct().Count()==14 && GameLanguage.Locales[0]=="enUS","every supported client locale has a name, English first");
        Check(!GameLanguage.Known("enGB") && !GameLanguage.Known("") && !GameLanguage.Known(null) && !GameLanguage.Known("../x"),"enGB (the client maps it to enUS) and junk are not offered");

        // The real layout on disk, as the plugin reads it from the client folder.
        string client=Path.Combine(temp,"language-client");
        foreach(string file in new[]{"Strings/enUS/GLOBAL.txt","Strings/itIT/GLOBAL.txt","Data/Win/asset_manifest_itit.unity3d","Strings/jaJP/GLOBAL.txt"}) {
            string path=Path.Combine(client,file.Replace('/',Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path,"x");
        }
        Check(GameLanguage.Installed(client).SequenceEqual(new[]{"enUS","itIT"}),"a client folder on disk: English and Italian, not the half-present Japanese");
        Check(GameLanguage.Installed(Path.Combine(temp,"no-such-client")).Count==0,"a missing client folder has no languages");

        // client.config as SoloStone ships it (repo root), and the way the client reads keys.
        string shipped="[Config]\nVersion = 3\n\n[Localization]\nLocale = enUS\n\n[Application]\nMode = Public\n\n[Aurora]\nClientCheck = 0\nEnv.Override = 1\nEnv = 127.0.0.1\nPort = 1119\n";
        Check(GameLanguage.ConfigLocale(shipped.Split('\n'))=="enUS","the shipped client.config says enUS");
        Check(GameLanguage.ConfigLocale(new[]{"[localization]","  locale=deDE  "})=="deDE","section and key names are not case-sensitive, values are trimmed");
        Check(GameLanguage.ConfigLocale(new[]{"[Aurora]","Locale = frFR"})==null,"Locale in another section is not the language");
        Check(GameLanguage.ConfigLocale(new[]{"[Log]","FileSizeLimit.Int=-1"})==null && GameLanguage.ConfigLocale(null)==null,"no Localization section: none");
        Check(GameLanguage.ClientDefault(null)=="enUS" && GameLanguage.ClientDefault("xxYY")=="enUS" && GameLanguage.ClientDefault("frFR")=="frFR","the client's own pick falls back to enUS like HearthstoneLocalization");

        // What the LOCALE launch option says at startup: null keeps the client exactly as shipped.
        var en=new List<string>{"enUS"}; var enDe=new List<string>{"enUS","deDE"}; var deOnly=new List<string>{"deDE","frFR"};
        Check(GameLanguage.StartLocale("","enUS",en)==null && GameLanguage.StartLocale(null,"enUS",enDe)==null,"nothing saved: the client picks its own language");
        Check(GameLanguage.StartLocale("deDE","enUS",enDe)=="deDE","a saved language with its files is used");
        Check(GameLanguage.StartLocale("deDE","enUS",en)==null,"a saved language without its files falls back to the client's own");
        Check(GameLanguage.StartLocale("WEB_TOKEN","enUS",enDe)==null && GameLanguage.StartLocale("de-DE","enUS",enDe)==null,"a saved value that is not a client locale is ignored");
        Check(GameLanguage.StartLocale("","enUS",deOnly)=="deDE","a copy without English starts in its first installed language");
        Check(GameLanguage.StartLocale("frFR","enUS",deOnly)=="frFR","a saved language still wins in a copy without English");
        Check(GameLanguage.StartLocale("deDE","enUS",new List<string>())==null && GameLanguage.StartLocale("deDE","enUS",null)==null,"no installed languages found: the client as it was");
        foreach(string saved in new[]{null,"","enUS","deDE","frFR","zhCN","x","LOCALE"})
            foreach(var installed in new[]{en,enDe,deOnly,new List<string>()}) {
                string start=GameLanguage.StartLocale(saved,"enUS",installed);
                Check(start==null || (GameLanguage.Known(start) && installed.Contains(start)),"start is null or an installed client locale: "+saved+" / "+string.Join(",",installed));
            }
        Check(GameLanguage.Running(null,"enUS")=="enUS" && GameLanguage.Running("deDE","enUS")=="deDE","running language: the launch option, else client.config");

        // One press offers the next installed language; English-only offers none.
        Check(GameLanguage.Offer(GameLanguage.Next("","enUS",en),en)==null,"English only: nothing to switch to");
        Check(GameLanguage.Offer(GameLanguage.Next("","enUS",enDe),enDe)=="deDE","English running, German installed: German is offered");
        Check(GameLanguage.Offer(GameLanguage.Next("deDE","enUS",enDe),enDe)=="enUS","German chosen: English is offered back");
        var three=new List<string>{"enUS","deDE","frFR"};
        Check(GameLanguage.Offer("deDE",three)=="frFR" && GameLanguage.Offer("frFR",three)=="enUS","offers cycle through every installed language");
        Check(GameLanguage.Offer("itIT",three)=="enUS","a next language that is not installed offers the first");

        Check(GameLanguage.Label("enUS","enUS")=="Language: English","the plank names the language");
        Check(GameLanguage.Label("enUS","deDE")=="Language: German (restart)","a pending change says it needs a restart");
        Check(GameLanguage.Locales.All(locale=>GameLanguage.Label("enUS",locale).Length<=32 && GameLanguage.Label("enUS",locale).All(c=>c<128)),"plank labels stay short and ASCII, so every client font draws them");
        string body=GameLanguage.SwitchBody("enUS","deDE"), only=GameLanguage.OnlyBody(en);
        Check(body.Contains("German") && body.Contains("English") && body.Contains("next time you start"),"the switch popup names both languages and when it applies");
        Check(only.Contains("English only") && only.Contains("Battle.net") && only.Contains("saves stay"),"the English-only popup says how to add a language");
        Check(!GameLanguage.OnlyBody(new List<string>()).Contains("only."),"no languages found reads sensibly");
        Check(new[]{body,only,GameLanguage.Label("enUS","deDE")}.All(text=>!text.Contains('—') && !text.Contains('–')),"no dashes in player copy");
        Check(GameLanguage.DialogButtonId(GameLanguage.PopupId,"confirm")=="language.switch" && GameLanguage.DialogButtonId(GameLanguage.PopupId,"cancel")=="language.cancel"
            && GameLanguage.DialogButtonId(GameLanguage.PopupId,"okay")=="language.ok" && GameLanguage.DialogButtonId("OTHER","confirm")==null,"test-control ids for the popup buttons only");
        Check(GameLanguage.IsDialogButtonId("language.ok") && GameLanguage.IsDialogButtonId("language.switch") && !GameLanguage.IsDialogButtonId("dialog.okay"),
            "the language popup's buttons are reviewed ids, so its Battle.net how-to text does not deny them");
        string protocol=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"plugin-source","TestControlProtocol.cs"));
        Check(protocol.Contains("|| GameLanguage.IsDialogButtonId(element.Id)"),"test control skips the popup-text denylist for the language popup's buttons");

        // The guard keeps every other launch option, login tokens included, null.
        foreach(string key in new[]{"WEB_TOKEN","REGION","locale","LOCALE ",null})
            Check(GameLanguage.LaunchValue(key,"deDE")==null,"no value for launch option "+(key??"null"));
        Check(GameLanguage.LaunchValue("LOCALE","deDE")=="deDE" && GameLanguage.LaunchValue("LOCALE",null)==null && GameLanguage.LaunchValue("LOCALE","../x")==null,"LOCALE carries only a client locale");
        string probe=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"plugin-source","OfflineProbe.cs"));
        Check(probe.Contains("harmony.Patch(launchOptions,\n            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(LaunchOption)));"),"GetLaunchOption goes through the language-aware guard");
        Check(probe.Contains("__result = NativeLanguageOption.LaunchOption(__0); } catch (Exception) { __result = null; }"),"the guard answers null when the language lookup fails");
    }
}
