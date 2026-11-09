using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

// The deck tracker's stats (plugin/DeckStats.cs): what a settled match records of the player's cards, how a deck's matches add up
// to HSReplay-style card rates, and the text the tracker shows.
static partial class Program
{
    static LocalMatchCardStats CardStats(string opponent, string[] offered, string[] kept, string[] opening, string[] drawn, string[] played) =>
        new LocalMatchCardStats { OpponentClass=opponent, Offered=offered.ToList(), Kept=kept.ToList(), Opening=opening.ToList(), Drawn=drawn.ToList(), Played=played.ToList() };

    static void DeckStatsTests() {
        // The opponent tracker's rows: copies seen per card, cheapest first, created cards apart, the public counts kept.
        DeckTrackerRows.Card Describe(string id) => new DeckTrackerRows.Card{Name=id=="L"?"Legend":"Card "+id,Cost=id=="L"?8:id=="X"?1:3,Legendary=id=="L"};
        var opponent=OpponentTrackerRows.Build(new[]{"A","L","X","A"},new[]{"T","T"},4,21,Describe);
        Check(opponent.Deck.Select(r=>r.Id+r.Count).SequenceEqual(new[]{"X1","A2","L1"}) && opponent.Deck[2].Legendary && opponent.Seen==4,"opponent cards from their deck: copies seen, cheapest first");
        Check(opponent.Created.Count==1 && opponent.Created[0].Id=="T" && opponent.Created[0].Count==2,"created cards counted apart");
        Check(opponent.Hand==4 && opponent.Remaining==21 && opponent.Summary=="hand=4 deck=21 seen=Xx1,Ax2,Lx1 created=Tx2","opponent counts and log line");
        var none=OpponentTrackerRows.Build(null,new string[]{null,""},0,30,Describe);
        Check(none.Deck.Count==0 && none.Created.Count==0 && none.Seen==0,"nothing seen yet");
        var win=CardStats("MAGE",new[]{"A","B","C"},new[]{"A","B"},new[]{"A","B","D"},new[]{"A","B","D","E","E"},new[]{"A","E"});
        var loss=CardStats("WARRIOR",new[]{"A","B","F"},new[]{"B"},new[]{"B","C","G"},new[]{"B","C","G"},new[]{"B"});
        var games=new List<DeckStats.Game>{
            new DeckStats.Game{Result="Won",Cards=win}, new DeckStats.Game{Result="Lost",Cards=loss},
            new DeckStats.Game{Result="Abandoned",Cards=null}, new DeckStats.Game{Result="Tied",Cards=win}, new DeckStats.Game{Result=null,Cards=win}
        };
        var stats=DeckStats.Of(games);
        Check(stats.Wins==1 && stats.Losses==2 && stats.Games==3,"won is a win, lost and abandoned are losses, tied and unsettled count nowhere");
        Check(stats.TrackedGames==2 && stats.TrackedWins==1,"only matches with card stats are a base for card rates");
        Check(stats.Against("MAGE").SequenceEqual(new[]{1,0}) && stats.Against("WARRIOR").SequenceEqual(new[]{0,1}) && stats.Against("PRIEST").SequenceEqual(new[]{0,0}),"record by opponent class");
        var a=stats.For("A"); var b=stats.For("B"); var e=stats.For("E");
        Check(a.OfferedGames==2 && a.KeptGames==1 && a.OpeningGames==1 && a.OpeningWins==1,"kept when offered, and in the opening hand");
        Check(b.OpeningGames==2 && b.OpeningWins==1 && b.PlayedGames==1 && b.PlayedWins==0,"a card's games across matches");
        Check(e.DrawnGames==1 && e.DrawnWins==1 && e.PlayedGames==1,"two copies drawn in one game count once");
        Check(stats.For("Z")==null && stats.For(null)==null,"an unseen card has no stats");
        Check(DeckStats.Of(null).Games==0 && DeckStats.Empty.TrackedGames==0,"no matches, no stats");

        // The stats screen: modes, the class of older matches from the opponent's deck, recent games newest first.
        var screen=DeckStats.Of(new List<DeckStats.Game>{
            new DeckStats.Game{Result="Won",Casual=false,OpponentClass="PRIEST",Settled="2026-11-01T10:00:00Z"},
            new DeckStats.Game{Result="Lost",Casual=true,OpponentClass="PRIEST",Settled="2026-11-03T10:00:00Z"},
            new DeckStats.Game{Result="Won",Casual=true,Cards=win,OpponentClass="ROGUE",Settled="2026-11-02T10:00:00Z"},
            new DeckStats.Game{Result="Tied",Settled="2026-11-04T10:00:00Z"} });
        Check(screen.RankedWins==1 && screen.RankedLosses==0 && screen.CasualWins==1 && screen.CasualLosses==1,"ranked and casual records");
        Check(screen.Against("PRIEST").SequenceEqual(new[]{1,1}) && screen.Against("MAGE").SequenceEqual(new[]{1,0}) && screen.Against("ROGUE").SequenceEqual(new[]{0,0}),
            "older matches take the opponent deck's class; card stats' class wins when both are known");
        Check(screen.Recent.Select(g=>g.Settled.Substring(8,2)).SequenceEqual(new[]{"03","02","01"}),"recent games newest first, ties excluded");
        Check(DeckStatsText.Result("Abandoned")=="Lost" && DeckStatsText.Day("not a date")=="","result words and dates");

        Check(DeckStatsText.Rate(0,0)=="–" && DeckStatsText.Rate(1,3)=="33%" && DeckStatsText.Rate(2,3)=="67%" && DeckStatsText.Rate(1,2)=="50%","rates are whole percents, a dash with no games");
        Check(DeckStatsText.Record(0,0)=="No games yet" && DeckStatsText.Record(12,7)=="12–7 · 63%","the record line");
        Check(DeckStatsText.Against("DEMONHUNTER",new[]{3,1})=="vs Demon Hunter 3–1" && DeckStatsText.Against(null,new[]{1,1})==null,"the class record");
        Check(DeckStatsText.ClassName("DEATHKNIGHT")=="Death Knight" && DeckStatsText.ClassName("MAGE")=="Mage","class names as the game writes them");
        var even=DeckStatsText.Shade(1,2); var good=DeckStatsText.Shade(7,10); var bad=DeckStatsText.Shade(3,10);
        Check(even.All(c=>Math.Abs(c-1f)<.001f) && good[1]>good[0] && bad[0]>bad[1],"50% is white, above is green, below is red");

        Check(LocalMatchCardStats.Valid(win) && LocalMatchCardStats.Valid(CardStats(null,new string[0],new string[0],new string[0],new string[0],new string[0])),"valid card stats");
        Check(!LocalMatchCardStats.Valid(null) && !LocalMatchCardStats.Valid(CardStats("mage",new string[0],new string[0],new string[0],new string[0],new string[0]))
            && !LocalMatchCardStats.Valid(CardStats(null,new[]{"A"},new[]{"A","B"},new string[0],new string[0],new string[0]))
            && !LocalMatchCardStats.Valid(CardStats(null,new string[0],new string[0],new[]{"A"},new string[0],new string[0]))
            && !LocalMatchCardStats.Valid(CardStats(null,new[]{"bad id"},new string[0],new string[0],new string[0],new string[0]))
            && !LocalMatchCardStats.Valid(new LocalMatchCardStats{Played=null}),"invalid card stats are refused");
        var copy=LocalMatchCardStats.Clone(win); copy.Drawn.Add("X");
        Check(win.Drawn.Count==5 && copy.OpponentClass=="MAGE","a clone is independent");

        // The profile stores them as XML; a round trip keeps every list, duplicates included.
        var serializer=new XmlSerializer(typeof(LocalMatchCardStats)); var text=new StringWriter();
        serializer.Serialize(text,win);
        var back=(LocalMatchCardStats)serializer.Deserialize(new StringReader(text.ToString()));
        Check(back.OpponentClass=="MAGE" && back.Drawn.SequenceEqual(win.Drawn) && back.Offered.SequenceEqual(win.Offered) && back.Played.SequenceEqual(win.Played),"XML round trip");
        Check(text.ToString().Contains("<Card>E</Card>"),"cards are Card elements");

        // The card art strip: the widest painted run inside the window, a column in from any white edge.
        bool[] Columns(string map) => map.Select(c=>c=='w').ToArray();
        Check(!ArtStrip.Painted(Columns("pppppppppp"),0,1,out _,out _),"no white: the window stays");
        Check(ArtStrip.Painted(Columns("ppppppppww"),0,1,out float f1,out float t1) && f1==0 && Math.Abs(t1-.7f)<.001f,"white at the right end is cut, one column in");
        Check(ArtStrip.Painted(Columns("wwppppppww"),0,1,out float f2,out float t2) && Math.Abs(f2-.3f)<.001f && Math.Abs(t2-.7f)<.001f,"white at both ends");
        Check(ArtStrip.Painted(Columns("ppppwpppppppw"),0,1,out float f3,out float t3) && f3>.3f && t3<1,"the widest run wins");
        Check(!ArtStrip.Painted(Columns("pwpwpwpwpw"),0,1,out _,out _),"no run wide enough: the window stays");
        Check(ArtStrip.Painted(Columns("ppppppppww"),.1f,.95f,out float f4,out float t4) && Math.Abs(f4-.1f)<.001f && t4<=.95f,"inside the bar's window");
    }
}
