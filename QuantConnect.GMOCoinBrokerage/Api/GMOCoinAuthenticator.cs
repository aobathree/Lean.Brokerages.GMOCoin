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
using System.Security.Cryptography;
using System.Text;

namespace QuantConnect.Brokerages.GMOCoin.Api
{
    /// <summary>
    /// Builds GMO Coin private REST API authentication headers.
    /// Signature = hex(HMAC-SHA256(secret, timestamp + method + path + body)) where
    /// path starts with /v1 (not /private) and excludes the query string, and body is
    /// the raw JSON request body (empty string for GET requests).
    /// See https://api.coin.z.com/docs/#authentication-private
    /// </summary>
    public class GMOCoinAuthenticator
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;

        /// <summary>
        /// Creates a new authenticator for the given credentials
        /// </summary>
        public GMOCoinAuthenticator(string apiKey, string apiSecret)
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;
        }

        /// <summary>
        /// True if credentials were provided
        /// </summary>
        public bool HasCredentials => !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_apiSecret);

        /// <summary>
        /// Returns the authentication headers for a request
        /// </summary>
        /// <param name="method">HTTP method, e.g. GET or POST</param>
        /// <param name="path">Request path starting with /v1, without the query string, e.g. /v1/account/assets</param>
        /// <param name="body">Raw JSON request body exactly as sent, empty string for GET</param>
        /// <param name="timestampMs">Optional request time override for testing</param>
        public Dictionary<string, string> GetHeaders(string method, string path, string body = "", long? timestampMs = null)
        {
            var timestamp = (timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToStringInvariant();
            var signature = Sign(_apiSecret, timestamp + method + path + body);
            return new Dictionary<string, string>
            {
                { "API-KEY", _apiKey },
                { "API-TIMESTAMP", timestamp },
                { "API-SIGN", signature }
            };
        }

        /// <summary>
        /// Computes the lowercase hex HMAC-SHA256 signature of the given message
        /// </summary>
        public static string Sign(string secret, string message)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
