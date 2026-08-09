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
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// Factory for creating <see cref="GMOCoinBrokerage"/> instances
    /// </summary>
    public class GMOCoinBrokerageFactory : BrokerageFactory
    {
        /// <summary>
        /// Creates a new factory instance
        /// </summary>
        public GMOCoinBrokerageFactory() : base(typeof(GMOCoinBrokerage))
        {
        }

        /// <summary>
        /// Gets the brokerage data required to run the brokerage from configuration.
        /// Credentials fall back to the GMOCOIN_API_KEY / GMOCOIN_API_SECRET environment
        /// variables when the config keys are empty, so secrets can be injected at runtime
        /// (1Password CLI locally, AWS SSM Parameter Store in AWS) without touching config.json.
        /// </summary>
        public override Dictionary<string, string> BrokerageData => new()
        {
            { "gmocoin-api-key", GetCredential("gmocoin-api-key", "GMOCOIN_API_KEY") },
            { "gmocoin-api-secret", GetCredential("gmocoin-api-secret", "GMOCOIN_API_SECRET") },
            { "gmocoin-rest-url", Config.Get("gmocoin-rest-url", "https://api.coin.z.com/private") },
            { "gmocoin-public-url", Config.Get("gmocoin-public-url", "https://api.coin.z.com/public") },
            { "gmocoin-websocket-url", Config.Get("gmocoin-websocket-url", "wss://api.coin.z.com/ws/public/v1") },
            { "gmocoin-private-websocket-url", Config.Get("gmocoin-private-websocket-url", "wss://api.coin.z.com/ws/private/v1") }
        };

        /// <summary>
        /// Reads a credential from configuration, falling back to an environment variable
        /// </summary>
        public static string GetCredential(string configKey, string environmentVariable)
        {
            var value = Config.Get(configKey);
            if (string.IsNullOrEmpty(value))
            {
                value = Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;
            }
            return value;
        }

        /// <summary>
        /// Gets the brokerage model
        /// </summary>
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
        {
            return new GMOCoinBrokerageModel();
        }

        /// <summary>
        /// Creates a new brokerage instance from the job packet
        /// </summary>
        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var errors = new List<string>();
            var apiKey = Read<string>(job.BrokerageData, "gmocoin-api-key", errors);
            var apiSecret = Read<string>(job.BrokerageData, "gmocoin-api-secret", errors);
            var restUrl = Read<string>(job.BrokerageData, "gmocoin-rest-url", errors);
            var publicUrl = Read<string>(job.BrokerageData, "gmocoin-public-url", errors);
            var webSocketUrl = Read<string>(job.BrokerageData, "gmocoin-websocket-url", errors);
            var privateWebSocketUrl = Read<string>(job.BrokerageData, "gmocoin-private-websocket-url", errors);

            if (errors.Count != 0)
            {
                throw new ArgumentException(string.Join(Environment.NewLine, errors));
            }

            var aggregator = Composer.Instance.GetPart<IDataAggregator>() ??
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                    Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), false);

            var brokerage = new GMOCoinBrokerage(apiKey, apiSecret, restUrl, publicUrl, webSocketUrl,
                privateWebSocketUrl, algorithm?.Transactions, aggregator);
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);
            return brokerage;
        }

        /// <summary>
        /// Performs cleanup of brokerage resources
        /// </summary>
        public override void Dispose()
        {
        }
    }
}
