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
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// History via the public klines endpoint. Minute and hour intervals are paged per
    /// GMO Coin trading day (a YYYYMMDD key covering 06:00 JST to 05:59 JST the next
    /// day, available from 2021-04-15), daily candles per year.
    /// </summary>
    public partial class GMOCoinBrokerage
    {
        // GMO Coin kline date keys use JST (UTC+9); intraday keys roll over at 06:00 JST
        private static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);
        private static readonly TimeSpan TradingDayStart = TimeSpan.FromHours(6);

        /// <summary>
        /// First trading day available from the intraday (YYYYMMDD) kline endpoint
        /// </summary>
        public static readonly DateTime MinIntradayDate = new(2021, 4, 15);

        private bool _historyWarningFired;

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (request.Symbol.SecurityType != SecurityType.Crypto || request.Symbol.ID.Market != GMOCoinMarket.Name)
            {
                if (!_historyWarningFired)
                {
                    _historyWarningFired = true;
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidHistoryRequest",
                        "GMOCoinBrokerage.GetHistory(): only GMO Coin Crypto symbols are supported"));
                }
                return null;
            }

            if (request.TickType != TickType.Trade)
            {
                return null;
            }

            string interval;
            switch (request.Resolution)
            {
                case Resolution.Minute:
                    interval = "1min";
                    break;
                case Resolution.Hour:
                    interval = "1hour";
                    break;
                case Resolution.Daily:
                    interval = "1day";
                    break;
                default:
                    if (!_historyWarningFired)
                    {
                        _historyWarningFired = true;
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidHistoryRequest",
                            $"GMOCoinBrokerage.GetHistory(): unsupported resolution {request.Resolution}, use Minute, Hour or Daily"));
                    }
                    return null;
            }

            return GetKlineHistory(request, interval);
        }

        private IEnumerable<BaseData> GetKlineHistory(HistoryRequest request, string interval)
        {
            var gmoSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var period = request.Resolution.ToTimeSpan();
            var startUtc = request.StartTimeUtc;
            var endUtc = request.EndTimeUtc;

            foreach (var dateKey in GetDateKeys(startUtc, endUtc, request.Resolution))
            {
                List<Messages.GMOCoinCandle> candles;
                try
                {
                    candles = _restApiClient.GetKlines(gmoSymbol, interval, dateKey);
                }
                catch (Exception e)
                {
                    Log.Error(e, $"GMOCoinBrokerage.GetHistory({gmoSymbol}, {interval}, {dateKey})");
                    continue;
                }

                foreach (var candle in candles)
                {
                    var barTimeUtc = QuantConnect.Time.UnixMillisecondTimeStampToDateTime(candle.OpenTime);
                    if (barTimeUtc < startUtc || barTimeUtc + period > endUtc)
                    {
                        continue;
                    }

                    yield return new TradeBar(
                        barTimeUtc.ConvertFromUtc(request.ExchangeHours.TimeZone),
                        request.Symbol,
                        candle.Open,
                        candle.High,
                        candle.Low,
                        candle.Close,
                        candle.Volume,
                        period);
                }
            }
        }

        /// <summary>
        /// Enumerates the kline endpoint date keys covering the requested UTC range.
        /// Intraday keys (Minute/Hour) name the GMO Coin trading day starting 06:00 JST
        /// and are clamped to the endpoint's first available day (2021-04-15);
        /// Daily uses one YYYY key per JST year.
        /// </summary>
        public static IEnumerable<string> GetDateKeys(DateTime startUtc, DateTime endUtc, Resolution resolution)
        {
            if (resolution == Resolution.Daily)
            {
                var startJst = startUtc + JstOffset;
                var endJst = endUtc + JstOffset;
                for (var year = startJst.Year; year <= endJst.Year; year++)
                {
                    yield return year.ToStringInvariant();
                }
            }
            else
            {
                // the trading-day key of a timestamp is the JST date after rolling back 6 hours
                var startKey = (startUtc + JstOffset - TradingDayStart).Date;
                var endKey = (endUtc + JstOffset - TradingDayStart).Date;
                if (startKey < MinIntradayDate)
                {
                    startKey = MinIntradayDate;
                }
                for (var date = startKey; date <= endKey; date = date.AddDays(1))
                {
                    yield return date.ToStringInvariant("yyyyMMdd");
                }
            }
        }
    }
}
