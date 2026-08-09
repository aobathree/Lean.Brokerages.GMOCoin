# 24-hour ATR grid experiment on GMO Coin spot pairs.
# Places a small post-only buy-limit grid BELOW the market on every tradable
# pair, sized so that the worst case stays inside a hard JPY loss budget.
# Each fill gets a take-profit limit sell; the stop-loss is software-monitored
# (GMO Coin spot has no OCO API): cancel the TP, market-sell the size.
# The run self-terminates after RUN_HOURS: cancel everything, liquidate, quit.
#
# Safety rails (this is a learning experiment, not investment advice):
#   - per-level worst-case losses are summed while placing the grid and the
#     grid stops growing at MAX_TOTAL_LOSS_BUDGET_JPY (default 800)
#   - EMERGENCY_LOSS_JPY (default 1000): if total equity drops this far below
#     the start, everything is liquidated and the algorithm quits immediately
#   - committed notional is capped at CASH_USE_LIMIT of starting cash
#   - no refills in the last REFILL_CUTOFF_HOURS of the run
#
# Restart note: this strategy is intentionally NOT restart-safe (a restart
# forgets grid state). It is meant for a single supervised 24h run via
#   scripts\run-live.cmd -AlgorithmName GMOCoinAtrGrid24H -AlgorithmFile /repo/examples/gmocoin_atr_grid_24h.py
# If it dies mid-run, cancel open orders / close positions from the GMO app.
import math
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.GMOCoinBrokerage")
from QuantConnect.Brokerages.GMOCoin import GMOCoinBrokerageModel, GMOCoinOrderProperties


class GridLevel:
    def __init__(self, symbol, buy_price, size, atr_value):
        self.symbol = symbol
        self.buy_price = buy_price
        self.size = size
        self.atr_value = atr_value
        self.buy_order_id = None
        self.tp_order_id = None
        self.entry_price = None
        self.sl_price = None
        self.state = "BUY_OPEN"   # BUY_OPEN -> HOLDING -> DONE / STOPPED / DEAD


class GMOCoinAtrGrid24H(QCAlgorithm):
    """ATR-spaced buy grid below market with TP limits and software stop-losses."""

    UNIVERSE = ["BTCJPY", "ETHJPY", "BCHJPY", "LTCJPY", "XRPJPY", "XLMJPY", "DOTJPY",
                "ATOMJPY", "FCRJPY", "ADAJPY", "LINKJPY", "DOGEJPY", "SOLJPY",
                "ASTRJPY", "NACJPY", "SUIJPY", "WILDJPY"]

    RUN_HOURS = 24
    REFILL_CUTOFF_HOURS = 2        # no new grid entries in the final stretch
    LEVELS_PER_PAIR = 2
    GRID_STEP_ATR = 1.0            # spacing below market, in hourly ATRs
    TP_ATR = 1.0                   # take-profit distance above entry
    SL_ATR = 2.0                   # stop-loss distance below entry (software)
    ATR_PERIOD = 14                # hourly bars

    TARGET_NOTIONAL_JPY = 300      # desired size per level (min lot may exceed it)
    MAX_NOTIONAL_PER_LEVEL_JPY = 1500   # skip levels whose min lot costs more
    MAX_TOTAL_LOSS_BUDGET_JPY = 800     # sum of per-level worst cases while placing
    EMERGENCY_LOSS_JPY = 1000           # hard equity floor: liquidate + quit
    CASH_USE_LIMIT = 0.85               # committed notional cap vs starting cash

    def initialize(self):
        self.set_time_zone("Asia/Tokyo")
        self.set_brokerage_model(GMOCoinBrokerageModel())

        if not self.live_mode:
            self.set_account_currency("JPY")
            self.set_cash(25_000)
            self.set_start_date(2026, 8, 5)
            self.set_end_date(2026, 8, 8)

        resolution = Resolution.MINUTE if self.live_mode else Resolution.HOUR

        # note: named _atr so it does not shadow QCAlgorithm's atr() helper
        self._atr = {}
        for ticker in self.UNIVERSE:
            symbol = self.add_crypto(ticker, resolution, "gmocoin").symbol
            self._atr[symbol] = self.ATR(symbol, self.ATR_PERIOD, MovingAverageType.WILDERS, Resolution.HOUR)

        self._levels_by_order = {}     # order id -> GridLevel
        self._levels = []
        self._grids_placed = set()
        self._est_loss_total = 0.0
        self._committed_jpy = 0.0
        self._start_equity = None
        self._run_started_at = None
        self._shutdown_started = None
        self._done = False

        self.schedule.on(self.date_rules.every_day(), self.time_rules.every(timedelta(minutes=1)), self._tick)
        self.set_warm_up(timedelta(hours=self.ATR_PERIOD * 2 + 4))

    def on_warmup_finished(self):
        self._run_started_at = self.utc_time
        self._start_equity = self.portfolio.total_portfolio_value
        ready = sum(1 for a in self._atr.values() if a.is_ready)
        self.log(f"AtrGrid24H: warm-up finished, ATR ready {ready}/{len(self._atr)}, "
                 f"equity {self._start_equity:.0f} JPY, run ends {self._run_started_at + timedelta(hours=self.RUN_HOURS):%Y-%m-%d %H:%M} UTC")

    # ------------------------------------------------------------------ setup
    def on_data(self, data: Slice):
        if self.is_warming_up or self._done or self._shutdown_started is not None:
            return
        for symbol, atr in self._atr.items():
            if symbol in self._grids_placed:
                continue
            price = self.securities[symbol].price
            if price > 0 and atr.is_ready:
                self._grids_placed.add(symbol)
                self._place_grid(symbol, price, atr.current.value)

    def _place_grid(self, symbol, price, atr_value):
        if atr_value <= 0:
            return
        props = GMOCoinOrderProperties()
        props.PostOnly = True
        properties = self.securities[symbol].symbol_properties
        placed = 0

        for i in range(1, self.LEVELS_PER_PAIR + 1):
            buy_price = self._round_price(price - i * self.GRID_STEP_ATR * atr_value, properties)
            if buy_price <= 0:
                continue
            size = self._round_size(self.TARGET_NOTIONAL_JPY / buy_price, properties)
            if size <= 0:
                continue
            notional = size * buy_price
            est_loss = size * self.SL_ATR * atr_value   # worst case: fill then stop out

            if notional > self.MAX_NOTIONAL_PER_LEVEL_JPY:
                self.log(f"AtrGrid24H: skip {symbol.value} L{i} (min lot notional {notional:.0f} JPY too large)")
                continue
            if self._est_loss_total + est_loss > self.MAX_TOTAL_LOSS_BUDGET_JPY:
                self.log(f"AtrGrid24H: loss budget reached ({self._est_loss_total:.0f} JPY), no more levels")
                return
            if self._committed_jpy + notional > self.CASH_USE_LIMIT * (self._start_equity or 0):
                self.log("AtrGrid24H: cash commitment cap reached, no more levels")
                return

            level = GridLevel(symbol, buy_price, size, atr_value)
            ticket = self.limit_order(symbol, size, buy_price, tag=f"grid L{i}", order_properties=props)
            level.buy_order_id = ticket.order_id
            self._levels_by_order[ticket.order_id] = level
            self._levels.append(level)
            self._est_loss_total += est_loss
            self._committed_jpy += notional
            placed += 1

        if placed:
            self.log(f"AtrGrid24H: {symbol.value} grid placed ({placed} levels, ATR {atr_value:.4g}, "
                     f"budget used {self._est_loss_total:.0f}/{self.MAX_TOTAL_LOSS_BUDGET_JPY} JPY)")

    # ------------------------------------------------------------- lifecycle
    def on_order_event(self, order_event: OrderEvent):
        level = self._levels_by_order.get(order_event.order_id)
        if level is None:
            return

        if order_event.order_id == level.buy_order_id:
            if order_event.status == OrderStatus.FILLED:
                level.entry_price = float(order_event.fill_price)
                level.sl_price = level.entry_price - self.SL_ATR * level.atr_value
                level.state = "HOLDING"
                tp_price = self._round_price(level.entry_price + self.TP_ATR * level.atr_value,
                                             self.securities[level.symbol].symbol_properties)
                props = GMOCoinOrderProperties()
                props.PostOnly = True
                ticket = self.limit_order(level.symbol, -level.size, tp_price, tag="grid TP",
                                          order_properties=props)
                level.tp_order_id = ticket.order_id
                self._levels_by_order[ticket.order_id] = level
                self.log(f"AtrGrid24H: {level.symbol.value} filled @ {level.entry_price:.6g}, "
                         f"TP {tp_price:.6g} / SL {level.sl_price:.6g}")
            elif order_event.status in (OrderStatus.CANCELED, OrderStatus.INVALID):
                if level.state == "BUY_OPEN":
                    level.state = "DEAD"   # e.g. post-only expired; leave the slot empty

        elif order_event.order_id == level.tp_order_id:
            if order_event.status == OrderStatus.FILLED:
                level.state = "DONE"
                profit = (float(order_event.fill_price) - level.entry_price) * level.size
                self.log(f"AtrGrid24H: {level.symbol.value} TP hit, gross +{profit:.1f} JPY")
                self._maybe_refill(level)

    def _maybe_refill(self, level):
        # re-arm the same level after a completed round trip (est-loss budget
        # was already consumed by the original placement, so no re-check)
        if self._shutdown_started is not None or self._done:
            return
        remaining = timedelta(hours=self.RUN_HOURS) - (self.utc_time - self._run_started_at)
        if remaining < timedelta(hours=self.REFILL_CUTOFF_HOURS):
            return
        props = GMOCoinOrderProperties()
        props.PostOnly = True
        new_level = GridLevel(level.symbol, level.buy_price, level.size, level.atr_value)
        ticket = self.limit_order(level.symbol, level.size, level.buy_price, tag="grid refill",
                                  order_properties=props)
        new_level.buy_order_id = ticket.order_id
        self._levels_by_order[ticket.order_id] = new_level
        self._levels.append(new_level)

    # ------------------------------------------------------------ monitoring
    def _tick(self):
        if self.is_warming_up or self._done or self._run_started_at is None:
            return

        # emergency brake: hard JPY floor for the whole experiment
        equity = self.portfolio.total_portfolio_value
        if self._shutdown_started is None and equity < self._start_equity - self.EMERGENCY_LOSS_JPY:
            self.log(f"AtrGrid24H: EMERGENCY STOP, equity {equity:.0f} JPY "
                     f"(< start {self._start_equity:.0f} - {self.EMERGENCY_LOSS_JPY})")
            self._begin_shutdown()

        # scheduled end of the experiment
        if self._shutdown_started is None and self.utc_time - self._run_started_at >= timedelta(hours=self.RUN_HOURS):
            self.log("AtrGrid24H: 24h run complete, shutting down")
            self._begin_shutdown()

        if self._shutdown_started is not None:
            self._finish_shutdown_when_flat()
            return

        # software stop-loss: cancel the TP, market-sell the level's size
        for level in self._levels:
            if level.state != "HOLDING":
                continue
            price = self.securities[level.symbol].price
            if price <= 0 or price > level.sl_price:
                continue
            level.state = "STOPPED"
            self.log(f"AtrGrid24H: {level.symbol.value} STOP @ {price:.6g} (SL {level.sl_price:.6g})")
            if level.tp_order_id is not None:
                ticket = self.transactions.get_order_ticket(level.tp_order_id)
                if ticket is not None and ticket.status not in (OrderStatus.FILLED, OrderStatus.CANCELED):
                    ticket.cancel()
            self.market_order(level.symbol, -level.size, tag="grid SL")

    def _begin_shutdown(self):
        self._shutdown_started = self.utc_time
        self.transactions.cancel_open_orders()
        for level in self._levels:
            if level.state == "BUY_OPEN":
                level.state = "DEAD"

    def _finish_shutdown_when_flat(self):
        # liquidate whatever is still held once cancels are through, then quit
        if not self.transactions.get_open_orders():
            invested = [s for s in self._atr if self.portfolio[s].invested]
            if not invested:
                self._done = True
                equity = self.portfolio.total_portfolio_value
                self.log(f"AtrGrid24H: FINISHED, equity {equity:.0f} JPY "
                         f"({equity - self._start_equity:+.0f} vs start)")
                self.quit("AtrGrid24H finished")
                return
            for symbol in invested:
                self.liquidate(symbol, tag="shutdown")
            return
        # live fills settle in seconds; hourly-bar backtests need up to an hour per fill
        timeout = timedelta(minutes=10) if self.live_mode else timedelta(hours=3)
        if self.utc_time - self._shutdown_started > timeout:
            self.log("AtrGrid24H: shutdown timeout, quitting with open state — "
                     "check the GMO Coin app for leftovers")
            self._done = True
            self.quit("AtrGrid24H shutdown timeout")

    # --------------------------------------------------------------- helpers
    @staticmethod
    def _round_price(price, properties):
        tick = float(properties.minimum_price_variation)
        if tick <= 0:
            return price
        return math.floor(price / tick) * tick

    @staticmethod
    def _round_size(size, properties):
        lot = float(properties.lot_size)
        minimum = float(properties.minimum_order_size or 0)
        if lot > 0:
            size = math.floor(size / lot) * lot
        if size < minimum:
            size = minimum
        return size
