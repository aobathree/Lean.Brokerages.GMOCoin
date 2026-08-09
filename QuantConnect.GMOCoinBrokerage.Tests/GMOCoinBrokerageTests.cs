/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.IO;
using NUnit.Framework;
using QuantConnect.Brokerages.GMOCoin.Api;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Configuration;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.GMOCoin.Tests
{
    [TestFixture]
    public class GMOCoinBrokerageTests
    {
        private GMOCoinBrokerage _brokerage;
        private Symbol _btcJpy;

        [OneTimeSetUp]
        public void SetUp()
        {
            // point the symbol properties / market hours databases at the repo Data folder
            var dataFolder = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Data"));
            Config.Set("data-folder", dataFolder);
            Globals.Reset();

            _brokerage = new GMOCoinBrokerage(string.Empty, string.Empty,
                "https://api.coin.z.com/private", "https://api.coin.z.com/public",
                "wss://api.coin.z.com/ws/public/v1", "wss://api.coin.z.com/ws/private/v1", null, null);
            _btcJpy = Symbol.Create("BTCJPY", SecurityType.Crypto, GMOCoinMarket.Name);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _brokerage?.Dispose();
        }

        [Test]
        public void AccountBaseCurrencyIsJpy()
        {
            Assert.AreEqual(Currencies.JPY, _brokerage.AccountBaseCurrency);
        }

        [Test]
        public void BuildsMarketOrderRequest()
        {
            var request = _brokerage.BuildOrderRequest(new MarketOrder(_btcJpy, 0.01m, DateTime.UtcNow));

            Assert.AreEqual("BTC", request.Symbol);
            Assert.AreEqual("MARKET", request.ExecutionType);
            Assert.AreEqual("BUY", request.Side);
            Assert.AreEqual("0.01", request.Size);
            Assert.IsNull(request.Price);
            Assert.IsNull(request.TimeInForce);
        }

        [Test]
        public void BuildsPostOnlyLimitOrderRequest()
        {
            var properties = new GMOCoinOrderProperties { PostOnly = true };
            var request = _brokerage.BuildOrderRequest(
                new LimitOrder(_btcJpy, -0.5m, 9000000m, DateTime.UtcNow, properties: properties));

            Assert.AreEqual("LIMIT", request.ExecutionType);
            Assert.AreEqual("SELL", request.Side);
            Assert.AreEqual("0.5", request.Size);
            Assert.AreEqual("9000000", request.Price);
            Assert.AreEqual("SOK", request.TimeInForce);
        }

        [Test]
        public void BuildsStopMarketOrderRequest()
        {
            var request = _brokerage.BuildOrderRequest(
                new StopMarketOrder(_btcJpy, 0.1m, 10000000m, DateTime.UtcNow));

            Assert.AreEqual("STOP", request.ExecutionType);
            Assert.AreEqual("10000000", request.Price);
            Assert.IsNull(request.TimeInForce);
        }

        [Test]
        public void RejectsStopLimitOrders()
        {
            Assert.Throws<NotSupportedException>(() => _brokerage.BuildOrderRequest(
                new StopLimitOrder(_btcJpy, 0.1m, 10000000m, 10100000m, DateTime.UtcNow)));
        }

        [Test]
        public void RejectsNonGtcTimeInForce()
        {
            var properties = new GMOCoinOrderProperties { TimeInForce = TimeInForce.Day };
            Assert.Throws<NotSupportedException>(() => _brokerage.BuildOrderRequest(
                new LimitOrder(_btcJpy, 1m, 100m, DateTime.UtcNow, properties: properties)));
        }

        [Test]
        public void ConvertsActiveLimitOrder()
        {
            var gmoOrder = new GMOCoinOrder
            {
                OrderId = 12345,
                Symbol = "BTC",
                Side = "SELL",
                ExecutionType = "LIMIT",
                Size = 1.0m,
                ExecutedSize = 0.6m,
                Price = 9500000m,
                Timestamp = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc),
                Status = GMOCoinOrderStatus.Ordered
            };

            var order = GMOCoinBrokerage.ConvertOrder(gmoOrder, _btcJpy);

            Assert.IsInstanceOf<LimitOrder>(order);
            Assert.AreEqual(-0.4m, order.Quantity); // remaining amount, sell = negative
            Assert.AreEqual(9500000m, ((LimitOrder)order).LimitPrice);
            Assert.AreEqual(OrderStatus.PartiallyFilled, order.Status);
            Assert.AreEqual("12345", order.BrokerId[0]);
        }

        [Test]
        public void ConvertsStopOrderAndRejectsUnknownTypes()
        {
            var stop = new GMOCoinOrder
            {
                OrderId = 2,
                Side = "BUY",
                ExecutionType = "STOP",
                Size = 0.2m,
                ExecutedSize = 0m,
                Price = 10000000m,
                Timestamp = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc),
                Status = GMOCoinOrderStatus.Waiting
            };
            var converted = GMOCoinBrokerage.ConvertOrder(stop, _btcJpy);
            Assert.IsInstanceOf<StopMarketOrder>(converted);
            Assert.AreEqual(10000000m, ((StopMarketOrder)converted).StopPrice);
            Assert.AreEqual(OrderStatus.Submitted, converted.Status);

            var unsupported = new GMOCoinOrder { ExecutionType = "IFDOCO" };
            Assert.IsNull(GMOCoinBrokerage.ConvertOrder(unsupported, _btcJpy));
        }

        [Test]
        public void ParsesSuccessAndErrorEnvelopes()
        {
            var data = GMOCoinRestApiClient.ParseResponse(
                "{\"status\":0,\"data\":{\"list\":[]},\"responsetime\":\"2019-03-19T02:15:06.081Z\"}", 200);
            Assert.IsNotNull(data["list"]);

            var exception = Assert.Throws<GMOCoinApiException>(() =>
                GMOCoinRestApiClient.ParseResponse(
                    "{\"status\":1,\"messages\":[{\"message_code\":\"ERR-5012\",\"message_string\":\"Invalid API-KEY.\"}]}", 200));
            Assert.AreEqual("ERR-5012", exception.ErrorCode);
            StringAssert.Contains("authentication failed", exception.Message);
        }

        [Test]
        public void HistoryDateKeysUseGmoTradingDays()
        {
            // 2026-01-01 20:00 UTC = 2026-01-02 05:00 JST, still inside trading day 20260101
            // (GMO Coin intraday kline days run 06:00 JST to 05:59 JST the next day)
            var start = new DateTime(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);
            var end = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc);

            var keys = new System.Collections.Generic.List<string>(
                GMOCoinBrokerage.GetDateKeys(start, end, Resolution.Minute));

            CollectionAssert.AreEqual(new[] { "20260101", "20260102" }, keys);

            var dailyKeys = new System.Collections.Generic.List<string>(
                GMOCoinBrokerage.GetDateKeys(new DateTime(2025, 12, 30), new DateTime(2026, 1, 5), Resolution.Daily));
            CollectionAssert.AreEqual(new[] { "2025", "2026" }, dailyKeys);
        }

        [Test]
        public void HistoryDateKeysClampToFirstAvailableDay()
        {
            var keys = new System.Collections.Generic.List<string>(
                GMOCoinBrokerage.GetDateKeys(new DateTime(2021, 4, 13), new DateTime(2021, 4, 15, 12, 0, 0), Resolution.Hour));
            CollectionAssert.AreEqual(new[] { "20210415" }, keys);
        }
    }
}
