# Momentum rotation across GMO Coin spot JPY pairs, designed for long-running
# live deployment (restart-safe) but also backtestable with daily data from
# tools/CandleDownloader.
#
# Strategy (sample code, not investment advice — tune and test before funding):
#   - Universe: 8 liquid GMO Coin spot pairs
#   - Signal: N-day momentum (rate of change, MOMP) on daily bars
#   - Daily at 09:10 JST: rank by momentum, keep the TOP_N assets whose
#     momentum is positive (absolute-momentum filter — otherwise stay in JPY)
#   - Cash-account-safe two-phase rebalance: the sell phase liquidates
#     rotated-out positions; the buy phase runs on a 15-minute check and only
#     acts once those sells have FILLED and no orders are in flight, so the
#     JPY proceeds are really available (sell-downs are likewise settled
#     before buy-ups). In a daily-bar backtest fills land on the next bar, so
#     a rotation takes a day or two; live fills settle in seconds.
#   - Small drifts (< MIN_TRADE_DELTA) are left alone to avoid fee churn
#
# Restart safety (systemd/docker restarts on AWS): the strategy keeps no local
# state — cash and open orders are restored from the brokerage by Lean, the
# momentum indicators re-warm from GMO Coin kline history on every start, and
# an interrupted rotation completes at the next day's rebalance.
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.GMOCoinBrokerage")
from QuantConnect.Brokerages.GMOCoin import GMOCoinBrokerageModel


class GMOCoinMomentumRotation(QCAlgorithm):
    """Daily momentum rotation: hold the TOP_N strongest JPY pairs, else cash."""

    UNIVERSE = ["BTCJPY", "ETHJPY", "XRPJPY", "SOLJPY", "LTCJPY", "ADAJPY", "DOGEJPY", "LINKJPY"]
    LOOKBACK_DAYS = 10        # momentum lookback (daily bars)
    TOP_N = 2                 # number of assets held
    TARGET_EXPOSURE = 0.90    # total exposure; the rest stays in JPY as a fee buffer
    MIN_TRADE_DELTA = 0.05    # skip rebalance trades smaller than 5% of portfolio value
    MAX_BUY_ATTEMPTS = 12     # give up on a stuck buy phase after ~3 hours

    def initialize(self):
        self.set_time_zone("Asia/Tokyo")
        self.set_brokerage_model(GMOCoinBrokerageModel())

        if not self.live_mode:
            # backtest defaults; in live mode the brokerage supplies currency and cash
            self.set_account_currency("JPY")
            self.set_cash(1_000_000)
            self.set_start_date(2024, 1, 1)

        # hourly data live (keeps the feed warm and prices fresh), daily in backtest
        resolution = Resolution.HOUR if self.live_mode else Resolution.DAILY

        self.momentum = {}
        for ticker in self.UNIVERSE:
            symbol = self.add_crypto(ticker, resolution, "gmocoin").symbol
            self.momentum[symbol] = self.momp(symbol, self.LOOKBACK_DAYS, Resolution.DAILY)

        self._pending_winners = None
        self._buy_attempts = 0

        # daily rotation shortly after the 06:00 JST GMO trading-day roll:
        # the sell phase runs at 09:10, the buy phase keeps checking every
        # 15 minutes and acts once the sale proceeds have settled
        self.schedule.on(self.date_rules.every_day(), self.time_rules.at(9, 10), self._rotate_sell)
        self.schedule.on(self.date_rules.every_day(), self.time_rules.every(timedelta(minutes=15)),
                         self._rotate_buy)

        # warm the momentum indicators from history (GMO klines via the brokerage
        # history provider in live mode, local files in backtests)
        self.set_warm_up(timedelta(days=self.LOOKBACK_DAYS + 3))

    def on_warmup_finished(self):
        ready = sum(1 for m in self.momentum.values() if m.is_ready)
        self.log(f"MomentumRotation: warm-up finished, {ready}/{len(self.momentum)} indicators ready")

    def _rotate_sell(self):
        if self.is_warming_up:
            return

        scores = {s: m.current.value for s, m in self.momentum.items() if m.is_ready}
        if not scores:
            self.log("MomentumRotation: no indicators ready yet, skipping rotation")
            return

        ranked = sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
        winners = [s for s, v in ranked[:self.TOP_N] if v > 0]
        self.log("MomentumRotation ranks: " +
                 ", ".join(f"{s.value}={v:.2f}%" for s, v in ranked) +
                 f" -> hold [{', '.join(s.value for s in winners) or 'JPY (cash)'}]")

        # phase 1: rotate out — free up JPY for the buy phase. Symbols with an
        # order already in flight (e.g. yesterday's unfilled liquidation) are
        # skipped: re-liquidating would stack a second sell on top of it.
        in_flight = {order.symbol for order in self.transactions.get_open_orders()}
        for symbol in self.momentum:
            if symbol not in winners and self.portfolio[symbol].invested and symbol not in in_flight:
                self.log(f"MomentumRotation: rotating out of {symbol.value}")
                self.liquidate(symbol, tag="rotate out")

        self._pending_winners = winners
        self._buy_attempts = 0

    def _rotate_buy(self):
        if self.is_warming_up or self._pending_winners is None:
            return
        winners = self._pending_winners

        # wait until the rotated-out positions are flat and nothing is in flight,
        # so the JPY proceeds are actually available in the cash account
        if any(self.portfolio[s].invested for s in self.momentum if s not in winners):
            return
        if self.transactions.get_open_orders():
            return
        if not winners:
            self._pending_winners = None
            return

        self._buy_attempts += 1
        if self._buy_attempts > self.MAX_BUY_ATTEMPTS:
            self.log("MomentumRotation: buy phase did not converge, retrying at the next rotation")
            self._pending_winners = None
            return

        weight = self.TARGET_EXPOSURE / len(winners)
        total_value = self.portfolio.total_portfolio_value
        if total_value <= 0:
            return

        adjustments = []
        for symbol in winners:
            current_weight = self.portfolio[symbol].holdings_value / total_value
            if abs(current_weight - weight) >= self.MIN_TRADE_DELTA:
                adjustments.append((symbol, current_weight))
        if not adjustments:
            # all targets reached: rotation complete
            self._pending_winners = None
            return

        # sell-downs first (they free the cash); buy-ups once nothing else is pending
        sell_downs = [a for a in adjustments if a[1] > weight]
        for symbol, current_weight in (sell_downs or adjustments):
            self.log(f"MomentumRotation: targeting {symbol.value} {current_weight:.1%} -> {weight:.1%}")
            self.set_holdings(symbol, weight, tag="rotate in")

    def on_order_event(self, order_event: OrderEvent):
        if order_event.status in (OrderStatus.FILLED, OrderStatus.PARTIALLY_FILLED,
                                  OrderStatus.CANCELED, OrderStatus.INVALID):
            self.log(f"MomentumRotation order event: {order_event}")
