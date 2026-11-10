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
        // Card chips: the opponent's hand by how each card came, hero power Imbues, quest progress.
        Check(CardChips.Round(1)==1 && CardChips.Round(2)==1 && CardChips.Round(3)==2 && CardChips.Round(8)==4 && CardChips.Round(0)==0,"a game turn counts in rounds, both players' turns as one");
        var kept=CardChips.Hand(new HandFacts{Origin=HandOrigin.Kept});
        var swapped=CardChips.Hand(new HandFacts{Origin=HandOrigin.Mulligan});
        var coin=CardChips.Hand(new HandFacts{Origin=HandOrigin.Coin});
        Check(kept.Label=="K" && swapped.Label=="M" && coin.Label=="Coin" && kept.Tags.Count==0 && coin.Detail.Contains("going second"),"opening hand: kept, from the mulligan, the Coin");
        var drawn=CardChips.Hand(new HandFacts{Origin=HandOrigin.Drawn,Turn=5});
        Check(drawn.Label=="T3" && drawn.Tags.Count==0 && drawn.Detail=="Drawn on turn 3.","a turn's draw: the round, no tag");
        var drawnBy=CardChips.Hand(new HandFacts{Origin=HandOrigin.Drawn,Turn=6,Source="Arcane Intellect"});
        Check(drawnBy.Label=="D3" && drawnBy.Tags.SequenceEqual(new[]{ChipTag.Drawn}) && drawnBy.Detail=="Drawn on turn 3 by Arcane Intellect.","drawn by a card the player saw");
        var made=CardChips.Hand(new HandFacts{Origin=HandOrigin.Created,Turn=4,Source="Fireball",Temporary=true});
        Check(made.Label=="C2" && made.Tags.SequenceEqual(new[]{ChipTag.Created,ChipTag.Temporary}) && made.Detail.StartsWith("Created on turn 2 by Fireball.\nTemporary"),"created and Temporary");
        var unknownSource=CardChips.Hand(new HandFacts{Origin=HandOrigin.Created,Turn=3});
        Check(unknownSource.Detail=="Created on turn 2.","an unseen creator is not named");
        var bounced=CardChips.Hand(new HandFacts{Origin=HandOrigin.Returned,Turn=7,Source="Sap",Known="Murloc Raider"});
        Check(bounced.Label=="R4" && bounced.Tags.SequenceEqual(new[]{ChipTag.Returned}) && bounced.Detail=="Returned to hand on turn 4 by Sap.\nIt is Murloc Raider.","returned: the card it is");
        var opening=CardChips.Hand(new HandFacts{Origin=HandOrigin.Created,Turn=0});
        Check(opening.Label=="C0" && opening.Detail=="Created at the start of the game.","created before the first turn");
        Check(CardChips.HeroPower(0)==null && CardChips.HeroPower(1).Detail.EndsWith("1 time.") && CardChips.HeroPower(3).Label=="Imbue 3","hero power: Imbues, none when never Imbued");
        Check(CardChips.Quest("Raid",2,5).Label=="2/5" && CardChips.Quest("Raid",2,5).Detail=="Raid: 2 of 5." && CardChips.Quest("X",0,0)==null,"quest progress");
        Check(CardChips.Summary(new Dictionary<int,Chip>{{12,made},{4,kept}})=="4:K 12:C2+Created+Temporary","chips log line");
        var trap=new SecretOption{Id="EX1_610",Name="Explosive Trap",Cost=2};
        var freeze=new SecretOption{Id="EX1_611",Name="Freezing Trap",Cost=2};
        var snake=new SecretOption{Id="EX1_554",Name="Snake Trap",Cost=2};
        var bear=new SecretOption{Id="AT_060",Name="Bear Trap",Cost=2};
        var secrets=new SecretCandidates();
        secrets.Track(30,new[]{trap,freeze,snake,trap});
        Check(secrets.Hidden==1 && secrets.Possible(30).Count==3 && secrets.Rows().Select(r=>r.Option.Name).SequenceEqual(new[]{"Explosive Trap","Freezing Trap","Snake Trap"}),"secrets: the pool, once each, by cost then name");
        Check(secrets.RuleOut(30,"EX1_610") && !secrets.RuleOut(30,"EX1_610") && !secrets.RuleOut(99,"EX1_611"),"secrets: a candidate is ruled out once, only for a tracked Secret");
        Check(secrets.Rows().Count==3 && secrets.Rows().Count(r=>r.Possible)==2 && !secrets.Rows().First(r=>r.Option.Id=="EX1_610").Possible,"secrets: a ruled-out candidate stays listed, not possible");
        secrets.Track(30,new[]{trap,bear});
        Check(secrets.Possible(30).Count==2,"secrets: tracking a Secret again keeps what was ruled out");
        secrets.Track(31,new[]{trap,freeze,snake});
        Check(secrets.Rows().First(r=>r.Option.Id=="EX1_610").Possible && secrets.Summary()=="30:2/3 31:3/3","secrets: possible while any hidden Secret could be it");
        secrets.Keep(new[]{31});
        Check(secrets.Hidden==1 && !secrets.Tracks(30) && secrets.Summary()=="31:3/3","secrets: a revealed Secret is forgotten");
        secrets.Keep(new int[0]);
        Check(secrets.Rows().Count==0 && secrets.Summary()=="","secrets: no hidden Secret, no list");
        Check(HistoryQuery.For("Battlecry: Cast a random spell for each spell you've cast this game (targets chosen randomly).").Kind==HistoryKind.Spells,"history: spells cast this game");
        Check(HistoryQuery.For("Battlecry: Repeat all other Battlecries you've played this game.").Kind==HistoryKind.Battlecries,"history: Battlecries played");
        var zoth=HistoryQuery.For("Battlecry: Summon your Deathrattle minions that died this game.");
        Check(zoth.Kind==HistoryKind.Died && zoth.Deathrattle && !zoth.Enemy && zoth.Race==null,"history: Deathrattle minions that died");
        var beasts=HistoryQuery.For("Deal 3 damage to an enemy minion. Summon a friendly Beast that died this game.");
        Check(beasts.Race=="BEAST" && !beasts.Enemy,"history: a type, and an earlier sentence's enemy does not count");
        Check(HistoryQuery.For("Summon a copy of an enemy minion that died this game.").Enemy,"history: enemy minions that died");
        var raise=HistoryQuery.For("Battlecry: Resurrect 2 friendly Deathrattle minions.");
        Check(raise.Kind==HistoryKind.Died && raise.Deathrattle,"history: Resurrect");
        Check(HistoryQuery.For("Deal 6 damage.")==null && HistoryQuery.For("")==null,"history: other cards ask nothing");
        var pool=new[]{new RecordedCard{Id="A",Name="Loot Hoarder",Deathrattle=true},new RecordedCard{Id="B",Name="Wisp"},
            new RecordedCard{Id="A",Name="Loot Hoarder",Deathrattle=true},new RecordedCard{Id="C",Name="Boar",Races=new[]{"BEAST"}}};
        var risen=zoth.Entries(pool);
        Check(risen.Count==1 && risen[0].Key.Name=="Loot Hoarder" && risen[0].Value==2 && zoth.Title(2)=="Friendly Deathrattle minions that died (2)","history: filtered, counted once per card");
        Check(beasts.Entries(pool).Single().Key.Id=="C" && HistoryQuery.For("Summon a friendly minion that died this game.").Entries(pool).Count==3,"history: by type, or all");
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
