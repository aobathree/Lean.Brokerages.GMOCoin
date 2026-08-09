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
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin.Api
{
    /// <summary>
    /// REST client for the GMO Coin public and private APIs.
    /// Private endpoints are rate limited per account (Tier 1: 20 req/s for GET and POST);
    /// this client stays well below that. All responses use the
    /// {"status": 0, "data": ..., "responsetime": ...} envelope.
    /// See https://api.coin.z.com/docs/
    /// </summary>
    public class GMOCoinRestApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly GMOCoinAuthenticator _authenticator;
        private readonly string _privateUrl;
        private readonly string _publicUrl;
        private readonly RateGate _queryRateGate = new(10, TimeSpan.FromSeconds(1));
        private readonly RateGate _updateRateGate = new(6, TimeSpan.FromSeconds(1));
        private readonly RateGate _publicRateGate = new(10, TimeSpan.FromSeconds(1));

        /// <summary>
        /// True if API credentials were provided
        /// </summary>
        public bool HasCredentials => _authenticator.HasCredentials;

        /// <summary>
        /// Creates a new REST client
        /// </summary>
        /// <param name="apiKey">GMO Coin API key, may be empty for public-data-only use</param>
        /// <param name="apiSecret">GMO Coin API secret</param>
        /// <param name="privateUrl">Private REST host, e.g. https://api.coin.z.com/private</param>
        /// <param name="publicUrl">Public REST host, e.g. https://api.coin.z.com/public</param>
        /// <param name="httpClient">Optional HttpClient override for testing</param>
        public GMOCoinRestApiClient(string apiKey, string apiSecret, string privateUrl, string publicUrl, HttpClient httpClient = null)
        {
            _authenticator = new GMOCoinAuthenticator(apiKey, apiSecret);
            _privateUrl = privateUrl.TrimEnd('/');
            _publicUrl = publicUrl.TrimEnd('/');
            _httpClient = httpClient ?? new HttpClient();
        }

        /// <summary>
        /// GET /v1/account/assets
        /// </summary>
        public List<GMOCoinAsset> GetAssets()
        {
            var data = PrivateGet("/v1/account/assets");
            return data.ToObject<List<GMOCoinAsset>>();
        }

        /// <summary>
        /// GET /v1/activeOrders for a single symbol. GMO Coin has no all-symbols variant,
        /// callers iterate over the symbols they care about.
        /// </summary>
        public List<GMOCoinOrder> GetActiveOrders(string symbol)
        {
            var orders = new List<GMOCoinOrder>();
            for (var page = 1; ; page++)
            {
                var data = PrivateGet("/v1/activeOrders", $"symbol={symbol}&page={page.ToStringInvariant()}&count=100");
                var list = data?["list"] as JArray;
                if (list == null || list.Count == 0)
                {
                    break;
                }
                orders.AddRange(list.ToObject<List<GMOCoinOrder>>());
                if (list.Count < 100)
                {
                    break;
                }
            }
            return orders;
        }

        /// <summary>
        /// GET /v1/orders for a single order id
        /// </summary>
        public GMOCoinOrder GetOrder(long orderId)
        {
            var data = PrivateGet("/v1/orders", $"orderId={orderId.ToStringInvariant()}");
            var list = data?["list"] as JArray;
            return list is { Count: > 0 } ? list[0].ToObject<GMOCoinOrder>() : null;
        }

        /// <summary>
        /// POST /v1/order: returns the new order id
        /// </summary>
        public long CreateOrder(GMOCoinOrderRequest request)
        {
            var data = PrivatePost("/v1/order", JsonConvert.SerializeObject(request));
            return long.Parse(data.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// POST /v1/cancelOrder
        /// </summary>
        public void CancelOrder(long orderId)
        {
            PrivatePost("/v1/cancelOrder", JsonConvert.SerializeObject(new { orderId }));
        }

        /// <summary>
        /// POST /v1/changeOrder: changes the price of an active LIMIT or STOP order
        /// </summary>
        public void ChangeOrder(long orderId, string price)
        {
            PrivatePost("/v1/changeOrder", JsonConvert.SerializeObject(new { orderId, price }));
        }

        /// <summary>
        /// POST /v1/ws-auth: creates a private WebSocket access token (60 minutes TTL)
        /// </summary>
        public string CreateWebSocketToken()
        {
            var data = PrivatePost("/v1/ws-auth", "{}");
            return data.ToString();
        }

        /// <summary>
        /// PUT /v1/ws-auth: extends a private WebSocket access token to a fresh 60 minutes TTL
        /// </summary>
        public void ExtendWebSocketToken(string token)
        {
            var body = JsonConvert.SerializeObject(new { token });
            _updateRateGate.WaitToProceed();
            SendSignedRequest(HttpMethod.Put, "/v1/ws-auth", body);
        }

        /// <summary>
        /// GET /public/v1/symbols (no auth): trading rules per symbol
        /// </summary>
        public JArray GetSymbols()
        {
            _publicRateGate.WaitToProceed();
            var response = SendRequest(new HttpRequestMessage(HttpMethod.Get, _publicUrl + "/v1/symbols"));
            return (JArray)response;
        }

        /// <summary>
        /// GET /public/v1/ticker (no auth) for a single symbol
        /// </summary>
        public JObject GetTicker(string symbol)
        {
            _publicRateGate.WaitToProceed();
            var response = SendRequest(new HttpRequestMessage(HttpMethod.Get, $"{_publicUrl}/v1/ticker?symbol={symbol}"));
            return (JObject)((JArray)response)[0];
        }

        /// <summary>
        /// GET /public/v1/klines. Returns an empty list when GMO Coin has no data for
        /// the requested date (error ERR-5207 covers invalid/out-of-range dates).
        /// </summary>
        /// <param name="symbol">GMO Coin symbol, e.g. BTC</param>
        /// <param name="interval">1min, 5min, 10min, 15min, 30min, 1hour, 4hour, 8hour, 12hour, 1day, 1week, 1month</param>
        /// <param name="date">YYYYMMDD for 1min..1hour (from 20210415, day starts 06:00 JST), YYYY for 4hour and above</param>
        public List<GMOCoinCandle> GetKlines(string symbol, string interval, string date)
        {
            _publicRateGate.WaitToProceed();
            JToken data;
            try
            {
                data = SendRequest(new HttpRequestMessage(HttpMethod.Get,
                    $"{_publicUrl}/v1/klines?symbol={symbol}&interval={interval}&date={date}"));
            }
            catch (GMOCoinApiException e) when (e.ErrorCode == "ERR-5207")
            {
                // no data for this date / date out of range
                return new List<GMOCoinCandle>();
            }
            return data.ToObject<List<GMOCoinCandle>>() ?? new List<GMOCoinCandle>();
        }

        private JToken PrivateGet(string path, string query = null)
        {
            _queryRateGate.WaitToProceed();
            var url = _privateUrl + path + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // the signature covers the path only, never the query string
            foreach (var header in _authenticator.GetHeaders("GET", path))
            {
                request.Headers.Add(header.Key, header.Value);
            }
            return SendRequest(request);
        }

        private JToken PrivatePost(string path, string body)
        {
            _updateRateGate.WaitToProceed();
            return SendSignedRequest(HttpMethod.Post, path, body);
        }

        private JToken SendSignedRequest(HttpMethod method, string path, string body)
        {
            var request = new HttpRequestMessage(method, _privateUrl + path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            foreach (var header in _authenticator.GetHeaders(method.Method, path, body))
            {
                request.Headers.Add(header.Key, header.Value);
            }
            return SendRequest(request);
        }

        private JToken SendRequest(HttpRequestMessage request)
        {
            using var response = _httpClient.SendAsync(request).SynchronouslyAwaitTaskResult();
            var content = response.Content.ReadAsStringAsync().SynchronouslyAwaitTaskResult();
            return ParseResponse(content, (int)response.StatusCode);
        }

        /// <summary>
        /// Parses the GMO Coin status/data envelope, throwing <see cref="GMOCoinApiException"/> on errors
        /// </summary>
        public static JToken ParseResponse(string content, int httpStatusCode)
        {
            JObject json;
            try
            {
                json = JObject.Parse(content);
            }
            catch (JsonException)
            {
                throw new GMOCoinApiException(string.Empty, $"Unexpected GMO Coin response (HTTP {httpStatusCode}): {content}");
            }

            if (json["status"]?.ToObject<int>() == 0)
            {
                return json["data"];
            }

            var firstMessage = (json["messages"] as JArray)?.Count > 0 ? json["messages"][0] : null;
            var code = firstMessage?["message_code"]?.ToString() ?? string.Empty;
            var text = firstMessage?["message_string"]?.ToString() ?? content;
            throw new GMOCoinApiException(code, $"GMO Coin API error {code} (HTTP {httpStatusCode}): {text} — {GetErrorDescription(code)}");
        }

        /// <summary>
        /// Human readable description for common GMO Coin error codes,
        /// see https://api.coin.z.com/docs/#error-code
        /// </summary>
        public static string GetErrorDescription(string code)
        {
            switch (code)
            {
                case "ERR-201": return "Insufficient trading balance";
                case "ERR-208": return "Insufficient holdings for the order quantity";
                case "ERR-254": return "Position not found";
                case "ERR-554": return "Server unavailable";
                case "ERR-626": return "Server busy, retry later";
                case "ERR-635": return "Too many active orders";
                case "ERR-5003": return "API call rate limit exceeded";
                case "ERR-5008": return "API-TIMESTAMP later than system time, sync your clock";
                case "ERR-5009": return "API-TIMESTAMP earlier than system time, sync your clock";
                case "ERR-5010": return "Invalid API-SIGN signature";
                case "ERR-5011": return "Missing API-KEY header";
                case "ERR-5012": return "API key authentication failed";
                case "ERR-5106": return "Invalid parameter";
                case "ERR-5111": return "Invalid timeInForce";
                case "ERR-5114": return "Size has more decimals than the symbol allows";
                case "ERR-5121": return "Order price too far from market";
                case "ERR-5122": return "Order is already modifying, cancelling, canceled, fully executed or expired";
                case "ERR-5123": return "Order id not found";
                case "ERR-5125": return "API trading is restricted";
                case "ERR-5126": return "Order size above the maximum or below the minimum";
                case "ERR-5129": return "Stop order price would execute immediately";
                case "ERR-5201": return "Scheduled maintenance in progress";
                case "ERR-5202": return "Emergency maintenance in progress";
                case "ERR-5203": return "Exchange is pre-open, orders not accepted";
                case "ERR-5206": return "Order change count limit reached, cancel and re-submit";
                case "ERR-5207": return "Invalid symbol, interval or date";
                default: return "See https://api.coin.z.com/docs/#error-code";
            }
        }

        /// <summary>
        /// Disposes rate gates and the HTTP client
        /// </summary>
        public void Dispose()
        {
            _queryRateGate.DisposeSafely();
            _updateRateGate.DisposeSafely();
            _publicRateGate.DisposeSafely();
            _httpClient.DisposeSafely();
        }
    }
}
