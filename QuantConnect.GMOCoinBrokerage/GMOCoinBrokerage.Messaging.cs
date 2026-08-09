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
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// Private WebSocket message processing: order lifecycle and fills
    /// </summary>
    public partial class GMOCoinBrokerage
    {
        private void ProcessPrivateMessage(JObject message)
        {
            try
            {
                switch (message["channel"]?.ToString())
                {
                    case "executionEvents":
                        HandleExecution(message.ToObject<GMOCoinExecutionEvent>());
                        break;

                    case "orderEvents":
                        HandleOrderEvent(message.ToObject<GMOCoinOrderEvent>());
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"GMOCoinBrokerage.ProcessPrivateMessage(): {message}");
            }
        }

        private void HandleExecution(GMOCoinExecutionEvent execution)
        {
            var order = FindOrder(execution.OrderId);
            if (order == null)
            {
                return;
            }

            // orderExecutedSize is the authoritative cumulative fill for this order
            var isCompleted = execution.OrderExecutedSize >= execution.OrderSize;
            var status = isCompleted ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

            var fee = OrderFee.Zero;
            if (execution.Fee != 0)
            {
                // fees are charged in JPY: positive for taker, negative (rebate) for maker
                fee = new OrderFee(new CashAmount(execution.Fee, Currencies.JPY));
            }

            var fillQuantity = execution.Side == "BUY" ? execution.ExecutionSize : -execution.ExecutionSize;
            OnOrderEvent(new OrderEvent(order, GMOCoinTime.ToUtc(execution.ExecutionTimestamp), fee,
                $"GMOCoin Order Event: {(execution.Fee < 0 ? "maker" : "taker")}")
            {
                Status = status,
                FillPrice = execution.ExecutionPrice,
                FillQuantity = fillQuantity
            });

            if (isCompleted)
            {
                _closedOrders.TryAdd(execution.OrderId, 0);
            }
        }

        private void HandleOrderEvent(GMOCoinOrderEvent orderEvent)
        {
            if (_closedOrders.ContainsKey(orderEvent.OrderId))
            {
                return;
            }

            var order = FindOrder(orderEvent.OrderId);
            if (order == null)
            {
                return;
            }

            // ROR: the matching engine rejected the new order
            if (orderEvent.MsgType == "ROR")
            {
                _closedOrders.TryAdd(orderEvent.OrderId, 0);
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "GMOCoin Order Event")
                {
                    Status = OrderStatus.Invalid,
                    Message = $"Order rejected by GMO Coin{FormatCancelType(orderEvent.CancelType)}"
                });
                return;
            }

            switch (orderEvent.OrderStatus)
            {
                case GMOCoinOrderStatus.Canceled:
                case GMOCoinOrderStatus.Expired:
                    _closedOrders.TryAdd(orderEvent.OrderId, 0);
                    // orderTimestamp is the order's creation time (verified live), not the
                    // cancellation time, so stamp the event with the current time instead
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                        "GMOCoin Order Event")
                    {
                        Status = OrderStatus.Canceled,
                        Message = orderEvent.OrderStatus == GMOCoinOrderStatus.Expired
                            ? $"Order expired{FormatCancelType(orderEvent.CancelType)}"
                            : null
                    });
                    break;

                // WAITING / ORDERED (msgType NOR): Submitted was already emitted on placement.
                // Fill events are emitted from executionEvents messages.
            }
        }

        private static string FormatCancelType(string cancelType)
        {
            return string.IsNullOrEmpty(cancelType) ? string.Empty : $" (cancelType: {cancelType})";
        }

        private Order FindOrder(long gmoOrderId)
        {
            var order = _orderProvider?.GetOrdersByBrokerageId(gmoOrderId)?.FirstOrDefault();
            if (order == null)
            {
                Log.Trace($"GMOCoinBrokerage: received event for unknown order id {gmoOrderId}");
            }
            return order;
        }
    }
}
