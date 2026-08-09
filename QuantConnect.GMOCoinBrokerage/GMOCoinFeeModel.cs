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
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// Provides an implementation of <see cref="FeeModel"/> that models GMO Coin spot order fees.
    /// GMO Coin charges fees in the quote currency (JPY) and pays a rebate to makers:
    /// the standard exchange rates are -0.03% maker / 0.09% taker (BTC, ETH and XRP
    /// currently run -0.01% / 0.05%). Actual per-symbol rates are published by the
    /// GET /public/v1/symbols endpoint (fields makerFee / takerFee); live fills report
    /// the exact fee via the private stream, so this model is an estimate for
    /// backtesting and pre-trade checks.
    /// </summary>
    public class GMOCoinFeeModel : FeeModel
    {
        /// <summary>
        /// Standard maker fee rate for most spot symbols (negative value = rebate)
        /// https://coin.z.com/jp/corp/guide/fees/
        /// </summary>
        public const decimal StandardMakerFee = -0.0003m;

        /// <summary>
        /// Standard taker fee rate for most spot symbols
        /// https://coin.z.com/jp/corp/guide/fees/
        /// </summary>
        public const decimal StandardTakerFee = 0.0009m;

        private readonly decimal _makerFee;

        private readonly decimal _takerFee;

        /// <summary>
        /// Creates GMO Coin fee model setting fee values
        /// </summary>
        /// <param name="makerFee">Maker fee rate, defaults to the standard -0.03% rebate</param>
        /// <param name="takerFee">Taker fee rate, defaults to the standard 0.09%</param>
        public GMOCoinFeeModel(decimal makerFee = StandardMakerFee, decimal takerFee = StandardTakerFee)
        {
            _makerFee = makerFee;
            _takerFee = takerFee;
        }

        /// <summary>
        /// Get the fee for this order in quote currency (JPY)
        /// </summary>
        /// <param name="parameters">A <see cref="OrderFeeParameters"/> object
        /// containing the security and order</param>
        /// <returns>The cost of the order in quote currency; negative for maker rebates</returns>
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters), "The 'parameters' argument cannot be null.");
            }

            var order = parameters.Order;
            var security = parameters.Security;
            var props = order.Properties as GMOCoinOrderProperties;

            // marketable limit orders are considered takers
            var isMaker = order.Type == OrderType.Limit && ((props != null && props.PostOnly) || !order.IsMarketable);
            var feePercentage = isMaker ? _makerFee : _takerFee;

            // get order value in quote currency, then apply maker/taker fee factor
            var unitPrice = order.Direction == OrderDirection.Buy ? security.AskPrice : security.BidPrice;
            unitPrice *= security.SymbolProperties.ContractMultiplier;

            var fee = unitPrice * order.AbsoluteQuantity * feePercentage;

            return new OrderFee(new CashAmount(fee, security.QuoteCurrency.Symbol));
        }
    }
}
