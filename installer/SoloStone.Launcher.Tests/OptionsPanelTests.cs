using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Options > SoloStone Options (plugin/OptionsPanelLayout.cs): where the Options plank may go and how the panel lays its rows
// out. Distances along the screen's right and up axes, y up.
static partial class Program
{
    static void OptionsPanelTests() {
        // HarmonyX runs every prefix even after one returns false, so each Escape prefix after the first must leave a key an earlier one
        // took (the Pace page's Escape reopens SoloStone Options; the panel's own prefix must not then send it on to Options).
        string plugin=Path.Combine(AppContext.BaseDirectory,"plugin-all");
        var escape=new System.Text.RegularExpressions.Regex(@"HandleEscapeKey""\)[^;]*?nameof\((\w+)\)\)(\s*\{\s*priority\s*=\s*Priority\.First)?");
        int prefixes=0;
        foreach(string file in Directory.GetFiles(plugin,"*.cs",SearchOption.AllDirectories)) {
            string source=File.ReadAllText(file);
            foreach(System.Text.RegularExpressions.Match patch in escape.Matches(source)) {
                prefixes++;
                if(patch.Groups[2].Success) continue;
                var method=System.Text.RegularExpressions.Regex.Match(source,@"static bool "+patch.Groups[1].Value+@"\(([^)]*)\)\s*\{\s*if \(!__runOriginal\) return false;");
                Check(method.Success,"Escape prefix "+Path.GetFileName(file)+" "+patch.Groups[1].Value+" leaves a key an earlier prefix took");
            }
        }
        Check(prefixes>=4,"the Escape prefixes were found: "+prefixes);

        // A panel frame 120 wide and 80 tall, the header plate dipping 6 into its top, the Back/Done bar under its bottom edge.
        var frame=new PanelRect(-60,-24,120,80);
        var region=OptionsPanelLayout.Region(frame,50,-30);
        Check(frame.Contains(region) && region.Top<50 && region.Y>frame.Y,"the free area is inside the frame and under the header");
        var raised=OptionsPanelLayout.Region(frame,60,-10);
        Check(raised.Y>-10 && raised.Top<frame.Top,"a bar inside the frame and a header above it: the area stays clear of both");

        // SoloStone Options: four checkboxes on the left, three planks on the right.
        var check=new PanelRow(30,9,true); var wide=new PanelRow(44,12,true); var plank=new PanelRow(38,10,false);
        var columns=new List<IList<PanelRow>>{ new List<PanelRow>{check,check,check,wide}, new List<PanelRow>{plank,plank,plank} };
        var plan=OptionsPanelLayout.Plan(region,columns);
        var all=plan.Rows.SelectMany(rows=>rows).ToList();
        Check(all.Count==7 && all.All(region.Contains),"every row lands inside the free area");
        Check(!all.Where((a,i)=>all.Skip(i+1).Any(b=>PanelRect.Overlap(a,b))).Any(),"no two rows overlap");
        Check(plan.Rows[0].Select(row=>row.X).Distinct().Count()==1,"checkboxes share one left edge");
        Check(plan.Rows[1].All(row=>Math.Abs(row.CenterX-(region.X+region.Width*.75f))<.01f),"planks sit centred in their column");
        Check(plan.Rows[0].Zip(plan.Rows[0].Skip(1),(a,b)=>a.CenterY>b.CenterY).All(x=>x),"rows run top to bottom in the order given");
        Check(Math.Abs(plan.Rows[0][0].CenterY-plan.Rows[1][0].CenterY)<.01f,"both columns start on the same line");
        Check(plan.Scale<=1,"rows never grow");

        // Crowded: rows shrink together until they fit; never below what fits, never overlapping.
        var tall=new PanelRow(30,30,true);
        var crowded=OptionsPanelLayout.Plan(region,new List<IList<PanelRow>>{ new List<PanelRow>{tall,tall,tall,tall} });
        var squeezed=crowded.Rows[0];
        Check(crowded.Scale<1 && squeezed.All(region.Contains),"too tall: the rows shrink and still fit");
        Check(!squeezed.Where((a,i)=>squeezed.Skip(i+1).Any(b=>PanelRect.Overlap(a,b))).Any(),"shrunk rows do not overlap");
        var long1=new PanelRow(400,9,true);
        Check(OptionsPanelLayout.Plan(region,new List<IList<PanelRow>>{ new List<PanelRow>{long1} }).Rows[0][0].Width<=region.Width,"too wide: the row shrinks to its column");

        // Three rows in one column: a compact block in the middle (not spread over the whole height).
        var few=OptionsPanelLayout.Plan(region,new List<IList<PanelRow>>{ new List<PanelRow>{check,check,check} }).Rows[0];
        float block=few[0].Top-few[2].Y, middle=(few[0].Top+few[2].Y)/2;
        Check(block<region.Height && Math.Abs(middle-region.CenterY)<.01f,"a short column sits as a block in the middle");
        Refuses(()=>OptionsPanelLayout.Plan(region,new List<IList<PanelRow>>()),"no columns");

        // Main Options: Screen Shake, the hidden spectator row over it, the hidden Miscellaneous plank under it.
        var shake=new PanelRect(10,0,40,8); var above=new PanelRect(10,12,40,8); var misc=new PanelRect(12,-14,36,10);
        var slots=OptionsPanelLayout.ButtonSlots(shake,above,misc,30,10);
        Check(slots.Select(slot=>slot.Key).SequenceEqual(new[]{"below","above"}),"under Screen Shake first, then over it");
        var below=slots[0].Value;
        Check(Math.Abs(below.CenterY-misc.CenterY)<.01f && Math.Abs(below.CenterX-misc.CenterX)<.01f && below.Top<=shake.Y,"below: the plank takes Miscellaneous's slot, clear of Screen Shake");
        Check(slots[1].Value.Y>=shake.Top,"above: the plank clears Screen Shake");
        var close=OptionsPanelLayout.ButtonSlots(shake,above,new PanelRect(12,-4,36,10),30,10)[0].Value;
        Check(close.Top<=shake.Y,"a Miscellaneous slot touching Screen Shake: the plank moves down to clear it");
        var none=OptionsPanelLayout.ButtonSlots(shake,above,null,30,10)[0].Value;
        Check(none.Top<=shake.Y && Math.Abs(none.CenterX-shake.CenterX)<.01f,"no Miscellaneous plank: under Screen Shake, centred on it");

        Check(OptionsPanelText.ButtonId(OptionsPanelText.SoloStoneKey)==OptionsPanelText.SoloStoneButtonId && OptionsPanelText.BackId(OptionsPanelText.SoloStoneKey)=="options.solostone.back"
            && OptionsPanelText.DismissId(OptionsPanelText.SoloStoneKey)=="options.solostone.dismiss" && OptionsPanelText.Title(OptionsPanelText.SoloStoneKey)=="SoloStone Options","panel ids and title");
        string protocol=PluginSource("TestControlProtocol.cs");
        Check(new[]{OptionsPanelText.SoloStoneKey}.All(key=>new[]{OptionsPanelText.ButtonId(key),OptionsPanelText.BackId(key),OptionsPanelText.DoneId(key),OptionsPanelText.DismissId(key)}
            .All(id=>protocol.Contains("\""+id+"\""))),"test control may open and close the panel in a live match");
    }
}
