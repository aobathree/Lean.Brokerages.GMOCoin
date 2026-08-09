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
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Brokerages.GMOCoin.Streaming;

namespace QuantConnect.Brokerages.GMOCoin.Tests
{
    [TestFixture]
    public class GMOCoinWebSocketProtocolTests
    {
        [Test]
        public void BuildsPublicSubscribeCommand()
        {
            Assert.AreEqual("{\"command\":\"subscribe\",\"channel\":\"trades\",\"symbol\":\"BTC\"}",
                GMOCoinPublicWebSocketClient.BuildCommand("subscribe", "trades", "BTC"));
            Assert.AreEqual("{\"command\":\"unsubscribe\",\"channel\":\"orderbooks\",\"symbol\":\"ETH\"}",
                GMOCoinPublicWebSocketClient.BuildCommand("unsubscribe", "orderbooks", "ETH"));
        }

        [Test]
        public void BuildsPrivateSubscribeCommand()
        {
            Assert.AreEqual("{\"command\":\"subscribe\",\"channel\":\"executionEvents\"}",
                GMOCoinPrivateWebSocketClient.BuildCommand("subscribe", "executionEvents"));
        }

        [Test]
        public void ParsesTradesMessageFromOfficialDocs()
        {
            // sample from https://api.coin.z.com/docs/#ws-trades
            const string frame = "{\"channel\":\"trades\",\"price\":\"750760\",\"side\":\"BUY\"," +
                "\"size\":\"0.1\",\"timestamp\":\"2018-03-30T12:34:56.789Z\",\"symbol\":\"BTC\"}";

            var trade = JObject.Parse(frame).ToObject<GMOCoinStreamTrade>();
            Assert.AreEqual("BTC", trade.Symbol);
            Assert.AreEqual(750760m, trade.Price);
            Assert.AreEqual(0.1m, trade.Size);
            Assert.AreEqual("BUY", trade.Side);
            Assert.AreEqual(new DateTime(2018, 3, 30, 12, 34, 56, 789, DateTimeKind.Utc), GMOCoinTime.ToUtc(trade.Timestamp));
        }

        [Test]
        public void ParsesOrderBookSnapshot()
        {
            const string frame = "{\"channel\":\"orderbooks\",\"asks\":[{\"price\":\"455659\",\"size\":\"0.1\"}," +
                "{\"price\":\"455665\",\"size\":\"0.3\"}],\"bids\":[{\"price\":\"455655\",\"size\":\"0.1\"}," +
                "{\"price\":\"455650\",\"size\":\"0.3\"}],\"symbol\":\"BTC\",\"timestamp\":\"2018-03-30T12:34:56.789Z\"}";

            var snapshot = JObject.Parse(frame).ToObject<GMOCoinOrderBookSnapshot>();
            Assert.AreEqual("BTC", snapshot.Symbol);
            // asks ascending from best, bids descending from best
            Assert.AreEqual(455659m, snapshot.Asks[0].Price);
            Assert.AreEqual(455655m, snapshot.Bids[0].Price);
            Assert.AreEqual(0.1m, snapshot.Asks[0].Size);
        }

        [Test]
        public void ParsesExecutionEventFromOfficialDocs()
        {
            // sample from https://api.coin.z.com/docs/#ws-execution-events
            const string frame = "{\"channel\":\"executionEvents\",\"orderId\":123456789,\"executionId\":72123911," +
                "\"symbol\":\"BTC\",\"settleType\":\"OPEN\",\"executionType\":\"LIMIT\",\"side\":\"BUY\"," +
                "\"executionPrice\":\"877404\",\"executionSize\":\"0.5\",\"positionId\":123456789," +
                "\"orderTimestamp\":\"2019-03-19T02:15:06.081Z\",\"executionTimestamp\":\"2019-03-19T02:15:06.081Z\"," +
                "\"lossGain\":\"0\",\"fee\":\"323\",\"orderPrice\":\"877200\",\"orderSize\":\"0.8\"," +
                "\"orderExecutedSize\":\"0.7\",\"timeInForce\":\"FAS\",\"msgType\":\"ER\"}";

            var execution = JObject.Parse(frame).ToObject<GMOCoinExecutionEvent>();
            Assert.AreEqual(123456789L, execution.OrderId);
            Assert.AreEqual(877404m, execution.ExecutionPrice);
            Assert.AreEqual(0.5m, execution.ExecutionSize);
            Assert.AreEqual(0.8m, execution.OrderSize);
            Assert.AreEqual(0.7m, execution.OrderExecutedSize);
            Assert.AreEqual(323m, execution.Fee);
            Assert.AreEqual("ER", execution.MsgType);
        }

        [Test]
        public void ParsesOrderEventFromOfficialDocs()
        {
            // sample from https://api.coin.z.com/docs/#ws-order-events
            const string frame = "{\"channel\":\"orderEvents\",\"orderId\":123456789,\"symbol\":\"BTC\"," +
                "\"settleType\":\"OPEN\",\"executionType\":\"LIMIT\",\"side\":\"BUY\",\"orderStatus\":\"CANCELED\"," +
                "\"cancelType\":\"USER\",\"orderTimestamp\":\"2019-03-19T02:15:06.081Z\",\"orderPrice\":\"876045\"," +
                "\"orderSize\":\"0.8\",\"orderExecutedSize\":\"0\",\"losscutPrice\":\"0\",\"timeInForce\":\"FAS\"," +
                "\"msgType\":\"NOR\"}";

            var orderEvent = JObject.Parse(frame).ToObject<GMOCoinOrderEvent>();
            Assert.AreEqual(123456789L, orderEvent.OrderId);
            Assert.AreEqual(GMOCoinOrderStatus.Canceled, orderEvent.OrderStatus);
            Assert.AreEqual("USER", orderEvent.CancelType);
            Assert.AreEqual(876045m, orderEvent.OrderPrice);
        }

        [Test]
        public void ParsesKlineCandles()
        {
            const string json = "[{\"openTime\":\"1618720200000\",\"open\":\"6289261\",\"high\":\"6290975\"," +
                "\"low\":\"6289261\",\"close\":\"6290975\",\"volume\":\"0.0113\"}]";

            var candles = JArray.Parse(json).ToObject<System.Collections.Generic.List<GMOCoinCandle>>();
            Assert.AreEqual(1, candles.Count);
            Assert.AreEqual(1618720200000L, candles[0].OpenTime);
            Assert.AreEqual(6289261m, candles[0].Open);
            Assert.AreEqual(0.0113m, candles[0].Volume);
        }

        [Test]
        public void NormalizesTimestampsToUtc()
        {
            var deserialized = JObject.Parse("{\"timestamp\":\"2019-03-19T02:15:06.081Z\"}")
                .ToObject<GMOCoinStreamTrade>();
            var parsed = GMOCoinTime.ToUtc(deserialized.Timestamp);
            Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
            Assert.AreEqual(new DateTime(2019, 3, 19, 2, 15, 6, 81), parsed);

            // missing timestamps fall back to now
            Assert.That((DateTime.UtcNow - GMOCoinTime.ToUtc(default)).Duration(), Is.LessThan(TimeSpan.FromSeconds(5)));
        }
    }
}
