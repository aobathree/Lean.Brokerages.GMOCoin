# Sample algorithm for the GMO Coin plugin on a stock (unmodified) LEAN build.
# Demonstrates: plugin assembly load, market registration, JPY account currency,
# GMOCoinBrokerageModel (fees, min order sizes) and daily-candle backtesting.
#
# Requires: QuantConnect.GMOCoinBrokerage.dll next to the Launcher, gmocoin rows
# in symbol-properties / market-hours (scripts/install-gmocoin-data.sh) and
# daily candles downloaded with tools/CandleDownloader.
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.GMOCoinBrokerage")
from QuantConnect.Brokerages.GMOCoin import GMOCoinBrokerageModel


class GMOCoinSmaCrossExample(QCAlgorithm):
    """Long-only golden-cross on BTC/JPY: hold BTC while SMA20 > SMA60."""

    def initialize(self):
        self.set_start_date(2024, 1, 1)
        self.set_end_date(2026, 8, 1)
        self.set_account_currency("JPY")
        self.set_cash(1_000_000)
        # instantiating the model also registers the "gmocoin" market (module initializer)
        self.set_brokerage_model(GMOCoinBrokerageModel())

        self.btc = self.add_crypto("BTCJPY", Resolution.DAILY, "gmocoin").symbol
        self.fast = self.sma(self.btc, 20, Resolution.DAILY)
        self.slow = self.sma(self.btc, 60, Resolution.DAILY)
        self.set_benchmark(self.btc)
        self.set_warm_up(60, Resolution.DAILY)

    def on_data(self, data: Slice):
        if self.is_warming_up or not self.slow.is_ready:
            return
        invested = self.portfolio[self.btc].invested
        if self.fast.current.value > self.slow.current.value and not invested:
            self.set_holdings(self.btc, 0.95)
        elif self.fast.current.value < self.slow.current.value and invested:
            self.liquidate(self.btc)
