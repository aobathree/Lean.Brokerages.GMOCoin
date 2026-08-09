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
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Benchmarks;
using QuantConnect.Orders.Fees;
using System.Collections.Generic;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// Provides GMO Coin (coin.z.com) specific properties. GMO Coin is a Japanese crypto
    /// exchange; this model covers spot trading on JPY-quoted symbols with a cash account.
    /// Orders support price-only amendment (POST /v1/changeOrder); quantity changes
    /// require cancel and re-submit.
    /// </summary>
    public class GMOCoinBrokerageModel : DefaultBrokerageModel
    {
        /// <summary>
        /// Notifies users that only the price of an order can be updated: GMO Coin's
        /// changeOrder endpoint cannot change the quantity.
        /// </summary>
        private readonly BrokerageMessageEvent _message = new(BrokerageMessageType.Warning, 0,
            "GMO Coin only supports updating the order price. To change the quantity, cancel and re-submit the order.");

        /// <summary>
        /// Order types supported by GMO Coin spot trading: market, limit and stop (逆指値)
        /// </summary>
        private readonly HashSet<OrderType> _supportedOrderTypes = new()
        {
            OrderType.Limit,
            OrderType.Market,
            OrderType.StopMarket
        };

        /// <summary>
        /// Gets a map of the default markets to be used for each security type
        /// </summary>
        public override IReadOnlyDictionary<SecurityType, string> DefaultMarkets => GetDefaultMarkets(GMOCoinMarket.Name);

        /// <summary>
        /// Initializes a new instance of the <see cref="GMOCoinBrokerageModel"/> class
        /// </summary>
        /// <param name="accountType">The type of account to be modelled, defaults to <see cref="AccountType.Cash"/></param>
        public GMOCoinBrokerageModel(AccountType accountType = AccountType.Cash)
            : base(accountType)
        {
            if (accountType == AccountType.Margin)
            {
                throw new ArgumentException("The GMO Coin brokerage does not currently support Margin trading.", nameof(accountType));
            }
        }

        /// <summary>
        /// GMO Coin global leverage rule: spot cash trading only
        /// </summary>
        public override decimal GetLeverage(Security security)
        {
            return 1m;
        }

        /// <summary>
        /// Get the benchmark for this model
        /// </summary>
        /// <param name="securities">SecurityService to create the security with if needed</param>
        /// <returns>The benchmark for this brokerage</returns>
        public override IBenchmark GetBenchmark(SecurityManager securities)
        {
            var symbol = Symbol.Create("BTCJPY", SecurityType.Crypto, GMOCoinMarket.Name);
            return SecurityBenchmark.CreateInstance(securities, symbol);
        }

        /// <summary>
        /// Provides GMO Coin fee model
        /// </summary>
        public override IFeeModel GetFeeModel(Security security)
        {
            return new GMOCoinFeeModel();
        }

        /// <summary>
        /// GMO Coin supports updating the order price only (POST /v1/changeOrder);
        /// quantity changes are rejected.
        /// </summary>
        /// <param name="security">The security of the order</param>
        /// <param name="order">The order to be updated</param>
        /// <param name="request">The requested update to be made to the order</param>
        /// <param name="message">If this function returns false, a brokerage message detailing why the order may not be updated</param>
        /// <returns>True for price-only updates of limit and stop orders</returns>
        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            if (request.Quantity.HasValue && request.Quantity.Value != order.Quantity)
            {
                message = _message;
                return false;
            }

            if (order.Type != OrderType.Limit && order.Type != OrderType.StopMarket)
            {
                message = _message;
                return false;
            }

            message = null;
            return true;
        }

        /// <summary>
        /// Evaluates whether the exchange will accept the order
        /// </summary>
        /// <param name="security">The security of the order</param>
        /// <param name="order">The order to be processed</param>
        /// <param name="message">If this function returns false, a brokerage message detailing why the order may not be submitted</param>
        /// <returns>True if the brokerage could process the order, false otherwise</returns>
        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            if (order == null || security == null)
            {
                var parameter = order == null ? nameof(order) : nameof(security);
                throw new ArgumentNullException(parameter, $"{parameter} parameter cannot be null. Please provide a valid {parameter} for submission.");
            }

            if (security.Type != SecurityType.Crypto)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    QuantConnect.Messages.DefaultBrokerageModel.UnsupportedSecurityType(this, security));
                return false;
            }

            if (!_supportedOrderTypes.Contains(order.Type))
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    QuantConnect.Messages.DefaultBrokerageModel.UnsupportedOrderType(this, order, _supportedOrderTypes));
                return false;
            }

            if (!IsValidOrderSize(security, order.Quantity, out message))
            {
                return false;
            }

            return base.CanSubmitOrder(security, order, out message);
        }

        /// <summary>
        /// Gets a new buying power model for the security; GMO Coin spot is cash-account only
        /// </summary>
        /// <param name="security">The security to get a buying power model for</param>
        /// <returns>The buying power model for this brokerage/security</returns>
        public override IBuyingPowerModel GetBuyingPowerModel(Security security)
        {
            return new CashBuyingPowerModel();
        }

        /// <summary>
        /// Gets the default markets for different security types, overriding the market name for Crypto securities.
        /// </summary>
        /// <param name="marketName">The default market name for Crypto securities.</param>
        protected static IReadOnlyDictionary<SecurityType, string> GetDefaultMarkets(string marketName)
        {
            var map = DefaultMarketMap.ToDictionary();
            map[SecurityType.Crypto] = marketName;
            return map.ToReadOnlyDictionary();
        }
    }
}
