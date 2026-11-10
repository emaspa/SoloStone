#if ENGINE_SOURCE
using EngineGame = SabberStoneCore.Model.Game;
using EngineController = SabberStoneCore.Model.Entities.Controller;
using EngineZone = SabberStoneCore.Enums.Zone;
using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;

// What a player knows their own deck still holds: the deck tracker's reading of the observations Sample uses.
// Known: copies the player can name. Unknown: slots the player can't name (an addition they didn't see, or a neutral
// slot Envoy turned into some Druid card). UnseenRemoved: cards that left the deck without the player seeing which,
// so up to that many of the slots above are gone. Remaining: the deck's size, which is public.
internal sealed class DeckKnowledge
{
    internal readonly IReadOnlyDictionary<string, int> Known;
    internal readonly int Unknown, UnseenRemoved, Remaining;
    internal DeckKnowledge(IReadOnlyDictionary<string, int> known, int unknown, int unseenRemoved, int remaining)
    { Known = known; Unknown = unknown; UnseenRemoved = unseenRemoved; Remaining = remaining; }
}

internal sealed partial class OwnDeckLedger
{
    // Sample's walk over the deck's moves, with nothing sampled: a departure the player didn't see removes no slot.
    internal DeckKnowledge Track(EngineGame game, EngineController player)
    {
        const string druidSlot = "?druid";
        var bag = player.DeckCards.Select(c => c.Id).ToList();
        var facts = game.HistoryFacts.Query.Events<HistoryFact>().ToArray();
        var moves = facts.OfType<ResidenceChangedFact>().Where(f => f.ControllerId == player.Id && f.Zone == EngineZone.DECK).ToArray();
        var residences = facts.OfType<ResidenceChangedFact>().ToLookup(f => f.EntityId);
        var publicCards = facts.OfType<HistoryCardFact>().Where(f => f.ControllerId == player.Id && f.DefinitionKnown).ToLookup(f => f.EntityId);
        var publicGraveyard = new HashSet<int>(player.GraveyardZone.Select(c => c.Id));
        var copies = player.DeckCards.Where(c => c.Id == "JAIL_384").SelectMany(_ =>
            player.DeckCards.Where(c => c.Id != "JAIL_384" && c[GameTag.ELITE] == 1)).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var initial = new HashSet<long>(moves.Where(f => f.Entered && f.Turn == 0).Take(player.DeckCards.Count).Select(f => f.Sequence));
        var additions = moves.Where(f => f.Entered && !initial.Contains(f.Sequence)).ToArray();
        long openingEnd = facts.OfType<TurnStartedFact>().Select(f => f.Sequence).DefaultIfEmpty(long.MaxValue).First();
        var startCopies = new HashSet<long>(additions.Where(f => f.Sequence < openingEnd).Reverse().Take(copies.Length).Select(f => f.Sequence));
        int copy = 0, unseen = 0;
        IReadOnlyList<SabberStoneCore.Model.Card> druidPool = null;
        bool Remove(string definition) { int index = bag.IndexOf(definition); if (index < 0) return false; bag.RemoveAt(index); return true; }
        var transforms = facts.OfType<CardPlayedFact>().Where(f => neutralTransforms.Contains(f.Sequence));
        foreach (var operation in moves.Where(f => !initial.Contains(f.Sequence)).Cast<HistoryFact>().Concat(transforms).OrderBy(f => f.Sequence))
        {
            if (operation is CardPlayedFact)
            {
                for (int i = 0; i < bag.Count; i++)
                    if (bag[i] != null && bag[i] != druidSlot && Cards.FromId(bag[i]).Class == CardClass.NEUTRAL) bag[i] = druidSlot;
                continue;
            }
            var move = (ResidenceChangedFact)operation;
            if (move.Entered)
            {
                if (startCopies.Contains(move.Sequence)) bag.Add(copies[copy++]);
                else
                {
                    var previousResidence = residences[move.EntityId].LastOrDefault(f => f.Sequence < move.Sequence);
                    var previousAppearance = publicCards[move.EntityId].LastOrDefault(f => f.Sequence < move.Sequence);
                    bool hiddenSource = previousResidence != null && previousResidence.ControllerId != player.Id
                        && (previousAppearance == null || previousAppearance.Sequence < previousResidence.Sequence);
                    bag.Add(moveDefinitions.TryGetValue(move.Sequence, out string addition) ? addition
                        : !hiddenSource && previousAppearance != null ? Cards.FromAssetId(previousAppearance.DbfId).Id : null);
                }
                continue;
            }
            var destination = residences[move.EntityId].FirstOrDefault(f => f.Sequence > move.Sequence);
            var appearance = publicCards[move.EntityId].FirstOrDefault(f => f.Sequence > move.Sequence);
            bool observed = destination != null && destination.Entered && destination.Zone == EngineZone.HAND && destination.ControllerId == player.Id
                || appearance != null && (destination == null || appearance.Sequence < destination.Sequence)
                || destination == null && publicGraveyard.Contains(move.EntityId);
            string departure = moveDefinitions.TryGetValue(move.Sequence, out string frozenDeparture) ? frozenDeparture
                : appearance != null && (destination == null || appearance.Sequence < destination.Sequence) ? Cards.FromAssetId(appearance.DbfId).Id : null;
            if ((observed || moveDefinitions.ContainsKey(move.Sequence)) && departure != null
                && (Remove(departure) || bag.Contains(druidSlot)
                    && (druidPool = druidPool ?? OfflineCardPools.Candidates(game, player.BaseClass,
                        new OfflinePoolFilter(classMode: PoolClassMode.Exact, exactClass: CardClass.DRUID), 116495)).Any(c => c.Id == departure)
                    && Remove(druidSlot) || Remove(null))) continue;
            unseen++;
        }
        int remaining = player.DeckZone.Count, unknown = bag.Count(d => d == null || d == druidSlot);
        // The count is public: slots beyond it went unseen, and a deck larger than the slots holds unknown cards.
        unseen = Math.Max(unseen, bag.Count - remaining);
        if (bag.Count - unseen < remaining) unknown += remaining - (bag.Count - unseen);
        var known = bag.Where(d => d != null && d != druidSlot).GroupBy(d => d, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new DeckKnowledge(known, unknown, unseen, remaining);
    }
}
#endif
