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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// IDataQueueHandler implementation: live trade ticks from the trades channel and
    /// quote ticks from the orderbooks channel (full snapshots, best bid/ask extracted)
    /// </summary>
    public partial class GMOCoinBrokerage
    {
        private readonly ConcurrentDictionary<string, Symbol> _subscribedSymbols = new();
        private readonly object _tickLocker = new();

        /// <summary>
        /// Sets the job we're subscribing for; initializes the instance when the engine
        /// created it via Composer for standalone data-queue-handler use
        /// </summary>
        public void SetJob(LiveNodePacket job)
        {
            var aggregator = _aggregator ?? Composer.Instance.GetPart<IDataAggregator>() ??
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                    Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), false);

            Initialize(
                job.BrokerageData.TryGetValue("gmocoin-api-key", out var apiKey) && !string.IsNullOrEmpty(apiKey)
                    ? apiKey : GMOCoinBrokerageFactory.GetCredential("gmocoin-api-key", "GMOCOIN_API_KEY"),
                job.BrokerageData.TryGetValue("gmocoin-api-secret", out var apiSecret) && !string.IsNullOrEmpty(apiSecret)
                    ? apiSecret : GMOCoinBrokerageFactory.GetCredential("gmocoin-api-secret", "GMOCOIN_API_SECRET"),
                job.BrokerageData.TryGetValue("gmocoin-rest-url", out var restUrl) ? restUrl : Config.Get("gmocoin-rest-url", "https://api.coin.z.com/private"),
                job.BrokerageData.TryGetValue("gmocoin-public-url", out var publicUrl) ? publicUrl : Config.Get("gmocoin-public-url", "https://api.coin.z.com/public"),
                job.BrokerageData.TryGetValue("gmocoin-websocket-url", out var wsUrl) ? wsUrl : Config.Get("gmocoin-websocket-url", "wss://api.coin.z.com/ws/public/v1"),
                job.BrokerageData.TryGetValue("gmocoin-private-websocket-url", out var privateWsUrl) ? privateWsUrl : Config.Get("gmocoin-private-websocket-url", "wss://api.coin.z.com/ws/private/v1"),
                _orderProvider,
                aggregator);

            if (!IsConnected)
            {
                Connect();
            }
        }

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);
            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        private static bool CanSubscribe(Symbol symbol)
        {
            return symbol.SecurityType == SecurityType.Crypto &&
                   symbol.ID.Market == GMOCoinMarket.Name &&
                   !symbol.Value.Contains("UNIVERSE", StringComparison.InvariantCulture);
        }

        private bool SubscribeChannels(IEnumerable<Symbol> symbols, TickType tickType)
        {
            foreach (var symbol in symbols)
            {
                var gmoSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                _subscribedSymbols[gmoSymbol] = symbol;

                var channel = tickType == TickType.Trade ? "trades" : "orderbooks";
                Log.Trace($"GMOCoinBrokerage.SubscribeChannels(): subscribing {channel}/{gmoSymbol}");
                _publicStreamClient.Subscribe(channel, gmoSymbol);
            }
            return true;
        }

        private bool UnsubscribeChannels(IEnumerable<Symbol> symbols, TickType tickType)
        {
            foreach (var symbol in symbols)
            {
                var gmoSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

                var channel = tickType == TickType.Trade ? "trades" : "orderbooks";
                _publicStreamClient.Unsubscribe(channel, gmoSymbol);

                if (!_subscriptionManager.IsSubscribed(symbol, TickType.Trade) &&
                    !_subscriptionManager.IsSubscribed(symbol, TickType.Quote))
                {
                    _subscribedSymbols.TryRemove(gmoSymbol, out _);
                }
            }
            return true;
        }

        private readonly ConcurrentDictionary<string, byte> _channelsSeen = new();

        private void OnStreamMessage(object sender, JObject message)
        {
            try
            {
                var channel = message["channel"]?.ToString();
                var gmoSymbol = message["symbol"]?.ToString();
                if (gmoSymbol == null || !_subscribedSymbols.TryGetValue(gmoSymbol, out var symbol))
                {
                    return;
                }

                if (_channelsSeen.TryAdd($"{channel}/{gmoSymbol}", 0))
                {
                    Log.Trace($"GMOCoinBrokerage.OnStreamMessage(): first message received for {channel}/{gmoSymbol}");
                }

                switch (channel)
                {
                    case "trades":
                        HandleTrade(message.ToObject<GMOCoinStreamTrade>(), symbol);
                        break;

                    case "orderbooks":
                        HandleOrderBook(message.ToObject<GMOCoinOrderBookSnapshot>(), symbol);
                        break;
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"GMOCoinBrokerage.OnStreamMessage({message["channel"]})");
            }
        }

        private void HandleTrade(GMOCoinStreamTrade trade, Symbol symbol)
        {
            var tick = new Tick
            {
                Symbol = symbol,
                Time = GMOCoinTime.ToUtc(trade.Timestamp),
                TickType = TickType.Trade,
                Value = trade.Price,
                Quantity = trade.Size
            };
            lock (_tickLocker)
            {
                _aggregator.Update(tick);
            }
        }

        private void HandleOrderBook(GMOCoinOrderBookSnapshot snapshot, Symbol symbol)
        {
            // full snapshot: asks ascending from best, bids descending from best
            if (snapshot.Bids == null || snapshot.Bids.Count == 0 ||
                snapshot.Asks == null || snapshot.Asks.Count == 0)
            {
                return;
            }

            var bestBid = snapshot.Bids[0];
            var bestAsk = snapshot.Asks[0];
            var tick = new Tick
            {
                Symbol = symbol,
                Time = GMOCoinTime.ToUtc(snapshot.Timestamp),
                TickType = TickType.Quote,
                BidPrice = bestBid.Price,
                BidSize = bestBid.Size,
                AskPrice = bestAsk.Price,
                AskSize = bestAsk.Size,
                Value = (bestBid.Price + bestAsk.Price) / 2m
            };
            lock (_tickLocker)
            {
                _aggregator.Update(tick);
            }
        }
    }
}
