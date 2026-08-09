# Live E2E test algorithm for the GMO Coin plugin on a stock LEAN build.
# PLACES ONE REAL ORDER: a minimum-lot (0.00001 BTC, ~100 JPY) post-only limit
# buy at 90% of the market price (GMO Coin rejects prices further away with
# ERR-5121; SOK post-only guarantees it cannot execute as a taker), then cancels
# it after 90 seconds and exits once the Canceled event flows back through the
# engine. Hard timeout: 300 seconds.
#
# Run via scripts/run-e2e.cmd (Windows) or scripts/run-e2e.sh (macOS/Linux).
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.GMOCoinBrokerage")
from QuantConnect.Brokerages.GMOCoin import GMOCoinBrokerageModel, GMOCoinOrderProperties


class GMOCoinE2ETestAlgorithm(QCAlgorithm):
    """Subscribe -> place min-lot post-only limit -> cancel -> confirm -> quit."""

    ORDER_SIZE = 0.00001          # GMO Coin minimum lot for spot BTC
    CANCEL_AFTER_SECONDS = 90
    TIMEOUT_SECONDS = 300

    def initialize(self):
        # account currency (JPY) and cash come from the brokerage in live mode
        self.set_brokerage_model(GMOCoinBrokerageModel())
        self.btc = self.add_crypto("BTCJPY", Resolution.SECOND, "gmocoin").symbol

        self._ticket = None
        self._placed_at = None
        self._cancel_requested = False
        self._finished = False
        self._started_at = self.utc_time
        self._data_count = 0

        # hard-timeout watchdog, independent of data arrival
        self.schedule.on(self.date_rules.every_day(), self.time_rules.every(timedelta(seconds=30)),
                         self._check_timeout)

    def on_data(self, data: Slice):
        self._data_count += 1
        if self._data_count == 1 or self._data_count % 60 == 0:
            self.log(f"E2E OnData #{self._data_count}")

        if self._finished or not data.contains_key(self.btc):
            return
        price = data[self.btc].price
        if price == 0:
            return

        if self._ticket is None:
            # -10%: inside GMO Coin's allowed limit-price band, far enough not to fill
            limit_price = int(price * 0.9)
            props = GMOCoinOrderProperties()
            props.PostOnly = True
            self.log(f"E2E placing post-only limit buy {self.ORDER_SIZE} BTC @ {limit_price} (market {price})")
            self._ticket = self.limit_order(self.btc, self.ORDER_SIZE, limit_price, order_properties=props)
            self._placed_at = self.utc_time
        elif (not self._cancel_requested and
              (self.utc_time - self._placed_at).total_seconds() > self.CANCEL_AFTER_SECONDS):
            self._cancel_requested = True
            self.log("E2E cancelling the order")
            self._ticket.cancel()

    def on_order_event(self, order_event: OrderEvent):
        self.log(f"E2E OnOrderEvent: {order_event}")
        if self._finished:
            return
        if order_event.status == OrderStatus.CANCELED:
            self._finished = True
            self.log("E2E: SUCCESS")
            self.quit("E2E SUCCESS")
        elif order_event.status == OrderStatus.INVALID:
            self._finished = True
            self.log("E2E: FAILED (order invalid)")
            self.quit("E2E FAILED")

    def _check_timeout(self):
        if not self._finished and (self.utc_time - self._started_at).total_seconds() > self.TIMEOUT_SECONDS:
            self._finished = True
            self.log(f"E2E: TIMEOUT after {self.TIMEOUT_SECONDS}s (data ticks: {self._data_count})")
            if self._ticket is not None:
                self._ticket.cancel()
            self.quit("E2E TIMEOUT")
