using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// The client's own words for SoloStone labels (plugin/NativeStrings.cs), and a guard that fails when plugin code
// hardcodes an English word the client already translates (OK, Back, Shop, class names...). Running the client in
// another language (Options > Language) then shows those labels in that language.
static partial class Program
{
    // English text -> the client key that translates it. Plain English words, no client data.
    static readonly Dictionary<string,string> LabelButtons=new Dictionary<string,string>(StringComparer.Ordinal) {
        {"OK","GLOBAL_OKAY"}, {"Back","GLOBAL_BACK"}, {"Cancel","GLOBAL_CANCEL"}, {"Continue","GLOBAL_CONTINUE"},
        {"Done","GLOBAL_DONE"}, {"Create","GLOBAL_CREATE"}, {"Delete","GLUE_COLLECTION_DECK_DELETE"},
        {"Close","GLOBAL_BUTTON_CLOSE"}, {"Next","GLOBAL_BUTTON_NEXT"},
    };
    static readonly Dictionary<string,string> LabelWords=new Dictionary<string,string>(StringComparer.Ordinal) {
        {"Shop","GLUE_STORE_OPEN_BUTTON_TEXT"}, {"Feats","GLUE_PROGRESSION_ACHIEVEMENTS_TITLE"}, {"Cards","GLUE_STORE_MODE_CARDS"},
        {"Packs","GLUE_BOX_PACKS"}, {"Card Backs","GLUE_COLLECTION_MANAGER_CARD_BACKS_TITLE"}, {"Coins","GLUE_COLLECTION_MANAGER_COINS_TITLE"},
        {"Pets","GLUE_COLLECTION_MANAGER_PET_TITLE"}, {"Incomplete deck","GLUE_INCOMPLETE_DECK_HEADER"}, {"Neutral","GLOBAL_CLASS_NEUTRAL"},
        {"Death Knight","GLOBAL_CLASS_DEATHKNIGHT"}, {"Demon Hunter","GLOBAL_CLASS_DEMONHUNTER"}, {"Druid","GLOBAL_CLASS_DRUID"},
        {"Hunter","GLOBAL_CLASS_HUNTER"}, {"Mage","GLOBAL_CLASS_MAGE"}, {"Paladin","GLOBAL_CLASS_PALADIN"}, {"Priest","GLOBAL_CLASS_PRIEST"},
        {"Rogue","GLOBAL_CLASS_ROGUE"}, {"Shaman","GLOBAL_CLASS_SHAMAN"}, {"Warlock","GLOBAL_CLASS_WARLOCK"}, {"Warrior","GLOBAL_CLASS_WARRIOR"},
    };
    static readonly string[] ClassWords={"Death Knight","Demon Hunter","Druid","Hunter","Mage","Paladin","Priest","Rogue","Shaman","Warlock","Warrior"};
    // file -> literals deliberately left in English, with the reason.
    static readonly Dictionary<string,Dictionary<string,string>> LabelAllowed=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal) {
        {"LocalCardCatalog.cs", ClassWords.ToDictionary(w=>w,w=>"LocalCoreProgression.ClassName: English for sentences and ids; headings wrap it in NativeStrings.ClassName")},
        {"NativeShop.cs", ClassWords.ToDictionary(w=>w,w=>"DeckClasses English labels: the fallback NativeStrings.ClassName(hero.Label) translates")},
        {"LocalCollectionUI.cs", new[]{"Shop","Packs","Next"}.ToDictionary(w=>w,w=>"legacy IMGUI collection screen; nothing opens it")},
        {"NativeResultsUI.cs", new Dictionary<string,string>{{"Continue","test-control element label, never drawn"}}},
    };
    // Lines where these words are ids, not labels: config sections, serialized names, binding tables, comparisons, cases, events.
    static readonly Regex LabelNotALabel=new Regex(@"Config\.Bind\(|XmlArray|new\[\] \{ ""(Field|Method)""|==\s*""[^""]*""\s*[?|&)]|case\s+""|Emit\(");
    static readonly Regex LabelToken=new Regex(@"""((?:[^""\\]|\\.)*)""|//[^\n]*|/\*.*?\*/",RegexOptions.Singleline);
    static readonly Regex LabelFallback=new Regex(@"NativeStrings\.(Get\(\s*""[A-Z0-9_]+""\s*,|ClassName\()\s*$");
    static readonly Regex LabelAssigned=new Regex(@"\b([A-Z]\w*)\s*=\s*$");

    static string GuardedLabel(string text) {
        if(LabelWords.ContainsKey(text) || LabelButtons.ContainsKey(text)) return text;
        return LabelButtons.Keys.FirstOrDefault(word=>text==word.ToUpperInvariant());
    }

    static List<string> HardcodedLabels(string pluginRoot) {
        var sources=Directory.GetFiles(pluginRoot,"*.cs",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal)
            .ToDictionary(p=>p.Substring(pluginRoot.Length+1).Replace(Path.DirectorySeparatorChar,'/'),File.ReadAllText);
        string everything=string.Join("\n",sources.Values);
        var found=new List<string>();
        foreach(var source in sources) {
            string text=source.Value;
            foreach(Match match in LabelToken.Matches(text)) {
                if(!match.Groups[1].Success) continue; // a comment
                string literal=match.Groups[1].Value, word=GuardedLabel(literal);
                if(word==null || (LabelAllowed.TryGetValue(source.Key,out var allowed) && allowed.ContainsKey(literal))) continue;
                int lineStart=text.LastIndexOf('\n',Math.Max(0,match.Index-1))+1, lineEnd=text.IndexOf('\n',match.Index);
                string before=text.Substring(lineStart,match.Index-lineStart), line=text.Substring(lineStart,(lineEnd<0?text.Length:lineEnd)-lineStart);
                if(LabelFallback.IsMatch(before) || LabelNotALabel.IsMatch(line)) continue;
                // A named English constant counts when a NativeStrings call uses that constant (its own class's) as the fallback.
                var named=LabelAssigned.Match(before);
                if(named.Success) {
                    var owner=Regex.Matches(LabelToken.Replace(text.Substring(0,match.Index),t=>t.Groups[1].Success?"\"\"":""),@"\b(?:class|struct)\s+(\w+)").Cast<Match>().LastOrDefault();
                    string name=Regex.Escape(named.Groups[1].Value);
                    if((owner!=null && Regex.IsMatch(everything,@"NativeStrings\.\w+\([^;]*\b"+Regex.Escape(owner.Groups[1].Value)+@"\."+name+@"\b"))
                        || Regex.IsMatch(text,@"NativeStrings\.\w+\([^;]*(?<![\w.])"+name+@"\b")) continue;
                }
                int number=text.Take(match.Index).Count(c=>c=='\n')+1;
                string key=LabelButtons.TryGetValue(word,out string k)?k:LabelWords[word];
                found.Add("plugin/"+source.Key+":"+number+" \""+literal+"\" -> NativeStrings.Get(\""+key+"\", \""+literal+"\")");
            }
        }
        return found;
    }

    static void NativeStringsTests(string temp) {
        Check(NativeStrings.Resolve("GLOBAL_BACK","Назад","Back")=="Назад","a translated key gives the client's word");
        Check(NativeStrings.Resolve("GLOBAL_BACK","GLOBAL_BACK","Back")=="Back","a missing key (the client echoes it) falls back to English");
        Check(NativeStrings.Resolve("GLOBAL_BACK",null,"Back")=="Back" && NativeStrings.Resolve("GLOBAL_BACK"," ","Back")=="Back","no text falls back to English");
        GameStrings.Translations["GLOBAL_CLASS_DEATHKNIGHT"]="Рыцарь смерти";
        try {
            Check(NativeStrings.ClassName("Death Knight")=="Рыцарь смерти","a class name maps to GLOBAL_CLASS_<NAME WITHOUT SPACES>");
            Check(NativeStrings.ClassName("Druid")=="Druid","an untranslated class keeps its English name");
        } finally { GameStrings.Translations.Clear(); }

        Check(GuardedLabel("BACK")=="Back" && GuardedLabel("packs")==null && GuardedLabel("MAGE")==null,"button words count in caps; ids and CardClass names do not");
        Check(LabelFallback.IsMatch("x = NativeStrings.Get(\"GLOBAL_BACK\", ") && !LabelFallback.IsMatch("x = SetText("),"a literal passed as the English fallback is allowed");
        string fake=Path.Combine(temp,"label-guard");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake,"Bad.cs"),"class A { void F(UIBButton close) { close.SetText(\"CLOSE\"); } }\n");
        File.WriteAllText(Path.Combine(fake,"Good.cs"),"class B { const string CloseLabel = \"CLOSE\"; void F(UIBButton c) { c.SetText(NativeStrings.Get(\"GLOBAL_BUTTON_CLOSE\", CloseLabel)); if (screen == \"Shop\") { } } }\n");
        var bad=HardcodedLabels(fake);
        Check(bad.Count==1 && bad[0].StartsWith("plugin/Bad.cs:1 \"CLOSE\""),"a bare button word is found with its file and line");

        var found=HardcodedLabels(Path.Combine(AppContext.BaseDirectory,"plugin-all"));
        Check(found.Count==0,"plugin code hardcodes no English the client translates:\n"+string.Join("\n",found));
    }
}

// Stand-in for the client's string table: a key without a translation comes back as itself, like the client's.
static class GameStrings
{
    internal static readonly Dictionary<string,string> Translations=new Dictionary<string,string>();
    internal static string Get(string key) => Translations.TryGetValue(key,out string value) ? value : key;
}
