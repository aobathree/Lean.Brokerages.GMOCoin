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
using System.Globalization;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.GMOCoin.Messages
{
    /// <summary>
    /// GMO Coin order statuses, see https://api.coin.z.com/docs/#orders
    /// </summary>
    public static class GMOCoinOrderStatus
    {
#pragma warning disable 1591
        // for stop (逆指値) orders WAITING means active, ORDERED means partially filled
        public const string Waiting = "WAITING";
        public const string Ordered = "ORDERED";
        public const string Modifying = "MODIFYING";
        public const string Cancelling = "CANCELLING";
        public const string Canceled = "CANCELED";
        public const string Executed = "EXECUTED";
        public const string Expired = "EXPIRED";
#pragma warning restore 1591
    }

    /// <summary>
    /// Exception carrying a GMO Coin API error code (e.g. ERR-5106), see
    /// https://api.coin.z.com/docs/#error-code
    /// </summary>
    public class GMOCoinApiException : Exception
    {
        /// <summary>
        /// GMO Coin error code such as "ERR-5122", empty when the failure was not a GMO Coin error envelope
        /// </summary>
        public string ErrorCode { get; }

        /// <summary>
        /// Creates a new exception for the given GMO Coin error code
        /// </summary>
        public GMOCoinApiException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode ?? string.Empty;
        }
    }

    /// <summary>
    /// A single asset balance from GET /v1/account/assets
    /// </summary>
    public class GMOCoinAsset
    {
#pragma warning disable 1591
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("available")]
        public decimal Available { get; set; }

        [JsonProperty("conversionRate")]
        public decimal ConversionRate { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// Order representation shared by GET /v1/activeOrders and GET /v1/orders
    /// </summary>
    public class GMOCoinOrder
    {
#pragma warning disable 1591
        [JsonProperty("orderId")]
        public long OrderId { get; set; }

        [JsonProperty("rootOrderId")]
        public long RootOrderId { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("orderType")]
        public string OrderType { get; set; }

        [JsonProperty("executionType")]
        public string ExecutionType { get; set; }

        [JsonProperty("settleType")]
        public string SettleType { get; set; }

        [JsonProperty("size")]
        public decimal Size { get; set; }

        [JsonProperty("executedSize")]
        public decimal ExecutedSize { get; set; }

        [JsonProperty("price")]
        public decimal? Price { get; set; }

        [JsonProperty("losscutPrice")]
        public decimal? LosscutPrice { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("cancelType")]
        public string CancelType { get; set; }

        [JsonProperty("timeInForce")]
        public string TimeInForce { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// Request body for POST /v1/order
    /// </summary>
    public class GMOCoinOrderRequest
    {
#pragma warning disable 1591
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("executionType")]
        public string ExecutionType { get; set; }

        [JsonProperty("timeInForce", NullValueHandling = NullValueHandling.Ignore)]
        public string TimeInForce { get; set; }

        [JsonProperty("price", NullValueHandling = NullValueHandling.Ignore)]
        public string Price { get; set; }

        [JsonProperty("size")]
        public string Size { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A private WebSocket executionEvents message (one execution, msgType ER)
    /// </summary>
    public class GMOCoinExecutionEvent
    {
#pragma warning disable 1591
        [JsonProperty("orderId")]
        public long OrderId { get; set; }

        [JsonProperty("executionId")]
        public long ExecutionId { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("executionType")]
        public string ExecutionType { get; set; }

        [JsonProperty("executionPrice")]
        public decimal ExecutionPrice { get; set; }

        [JsonProperty("executionSize")]
        public decimal ExecutionSize { get; set; }

        [JsonProperty("orderPrice")]
        public decimal? OrderPrice { get; set; }

        [JsonProperty("orderSize")]
        public decimal OrderSize { get; set; }

        [JsonProperty("orderExecutedSize")]
        public decimal OrderExecutedSize { get; set; }

        /// <summary>Fee in JPY: positive for taker, negative (rebate) for maker</summary>
        [JsonProperty("fee")]
        public decimal Fee { get; set; }

        [JsonProperty("timeInForce")]
        public string TimeInForce { get; set; }

        [JsonProperty("executionTimestamp")]
        public DateTime ExecutionTimestamp { get; set; }

        [JsonProperty("msgType")]
        public string MsgType { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A private WebSocket orderEvents message (msgType NOR/ROR/COR)
    /// </summary>
    public class GMOCoinOrderEvent
    {
#pragma warning disable 1591
        [JsonProperty("orderId")]
        public long OrderId { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("executionType")]
        public string ExecutionType { get; set; }

        [JsonProperty("orderStatus")]
        public string OrderStatus { get; set; }

        [JsonProperty("cancelType")]
        public string CancelType { get; set; }

        [JsonProperty("orderTimestamp")]
        public DateTime OrderTimestamp { get; set; }

        [JsonProperty("orderPrice")]
        public decimal? OrderPrice { get; set; }

        [JsonProperty("orderSize")]
        public decimal OrderSize { get; set; }

        [JsonProperty("orderExecutedSize")]
        public decimal OrderExecutedSize { get; set; }

        [JsonProperty("timeInForce")]
        public string TimeInForce { get; set; }

        [JsonProperty("msgType")]
        public string MsgType { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A public WebSocket trades channel message (one execution)
    /// </summary>
    public class GMOCoinStreamTrade
    {
#pragma warning disable 1591
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("size")]
        public decimal Size { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A single order book level: {"price": "...", "size": "..."}
    /// </summary>
    public class GMOCoinOrderBookLevel
    {
#pragma warning disable 1591
        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("size")]
        public decimal Size { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A public WebSocket orderbooks channel message: full snapshot,
    /// asks ascending from best, bids descending from best
    /// </summary>
    public class GMOCoinOrderBookSnapshot
    {
#pragma warning disable 1591
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("asks")]
        public List<GMOCoinOrderBookLevel> Asks { get; set; }

        [JsonProperty("bids")]
        public List<GMOCoinOrderBookLevel> Bids { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A single candle from GET /public/v1/klines
    /// </summary>
    public class GMOCoinCandle
    {
#pragma warning disable 1591
        [JsonProperty("openTime")]
        public long OpenTime { get; set; }

        [JsonProperty("open")]
        public decimal Open { get; set; }

        [JsonProperty("high")]
        public decimal High { get; set; }

        [JsonProperty("low")]
        public decimal Low { get; set; }

        [JsonProperty("close")]
        public decimal Close { get; set; }

        [JsonProperty("volume")]
        public decimal Volume { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// Shared helpers for GMO Coin message handling
    /// </summary>
    public static class GMOCoinTime
    {
        /// <summary>
        /// Normalizes a deserialized GMO Coin timestamp to UTC, falling back to the
        /// current time for missing (default) values
        /// </summary>
        public static DateTime ToUtc(DateTime timestamp)
        {
            if (timestamp == default)
            {
                return DateTime.UtcNow;
            }
            return timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        }
    }
}
