using System;
using System.Linq;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model.Entities;

namespace SabberStoneCore.Tasks.PlayerTasks
{
	public sealed class TradeCardTask : PlayerTask
	{
		private TradeCardTask(Controller controller, IPlayable source)
		{
			PlayerTaskType = PlayerTaskType.TRADE_CARD;
			Game = controller.Game;
			Controller = controller;
			Source = source;
		}

		public static TradeCardTask Any(Controller controller, IPlayable source)
		{
			return new TradeCardTask(controller, source);
		}

		public static bool IsValid(Controller controller, IPlayable source)
		{
			return controller != null && source != null &&
				controller.Game.State == State.RUNNING &&
				controller.Game.Step == Step.MAIN_ACTION &&
				controller.Choice == null &&
				controller.Game.CurrentPlayer == controller && source.Controller == controller &&
				source.Zone == controller.HandZone && source.Card[GameTag.TRADEABLE] == 1 &&
				Cost(source) >= 0 && controller.RemainingMana >= Cost(source) && controller.DeckZone.Count > 0;
		}

		private static int Cost(IPlayable source) =>
			source.Card.Tags.TryGetValue(GameTag.DECK_ACTION_COST, out int cost) ? cost : 1;

		public override bool Process()
		{
			if (!IsValid(Controller, Source))
				return false;

			IPlayable replacement = Controller.DeckZone.TopCard;
			if (Game.History)
				Game.PowerHistory.Add(PowerHistoryBuilder.BlockStart(BlockType.DECK_ACTION, Source.Id, "", 0, 0));

			int cost = Cost(Source);
			int temporary = Math.Min(Controller.TemporaryMana, cost);
			Controller.TemporaryMana -= temporary;
			Controller.UsedMana += cost - temporary;
			Controller.TotalManaSpentThisGame += cost;
            Game.Facts.Paid(Controller,Source,cost,action:SabberStoneCore.Model.HistoryPaymentKind.Trade);
			Source[GameTag.IS_USING_TRADE_OPTION] = 1;
			if (Game.History)
				Game.PowerHistory.Add(PowerHistoryBuilder.TagChange(Source.Id, GameTag.IS_USING_TRADE_OPTION, 1));
			if (Game.History)
				Game.PowerHistory.Add(PowerHistoryBuilder.ShowEntity(Source));
			using (Controller.Game.Facts.Shuffle(0))
				Controller.DeckZone.AddAtRandomPosition(Controller.HandZone.RemoveForTrade(Source));
			if (Game.History)
				Game.PowerHistory.Add(PowerHistoryBuilder.HideEntity(Source));
			Source[GameTag.IS_USING_TRADE_OPTION] = 0;
			if (Game.History)
				Game.PowerHistory.Add(PowerHistoryBuilder.TagChange(Source.Id, GameTag.IS_USING_TRADE_OPTION, 0));
            QuestRules.SettleHandCountChanges(Controller);
			Generic.Draw(Controller, Controller.DeckZone.ToList().IndexOf(replacement));
			// The draw can summon (Eredar Deceptor's Felbat). "After you summon" triggers such as Collapsing Star's refresh
			// fire in the cleanup step, as after any other action, and so do deaths.
			Game.DeathProcessingAndAuraUpdate();

			if (Game.History)
				Game.PowerHistory.Add(PowerHistoryBuilder.BlockEnd());
			return true;
		}

		public override string FullPrint()
		{
			return $"TradeCardTask => [{Controller.Name}] trade {Source}";
		}
	}
}
