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
using System.Linq;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.GMOCoin.Api;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Brokerages.GMOCoin.Streaming;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin
{
    /// <summary>
    /// GMO Coin (coin.z.com) brokerage implementation: spot trading on JPY-quoted symbols.
    /// Orders and balances use the private REST API, order/fill events arrive over the
    /// token-authenticated private WebSocket, and market data over the public WebSocket.
    /// </summary>
    [BrokerageFactory(typeof(GMOCoinBrokerageFactory))]
    public partial class GMOCoinBrokerage : Brokerage, IDataQueueHandler
    {
        private GMOCoinRestApiClient _restApiClient;
        private GMOCoinPublicWebSocketClient _publicStreamClient;
        private GMOCoinPrivateWebSocketClient _privateStreamClient;
        private ISymbolMapper _symbolMapper;
        private IOrderProvider _orderProvider;
        private IDataAggregator _aggregator;
        private BrokerageConcurrentMessageHandler<JObject> _messageHandler;
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

        // GMO Coin order ids for which a terminal order event (Filled/Canceled/Invalid) was already emitted
        private readonly ConcurrentDictionary<long, byte> _closedOrders = new();

        private bool _isInitialized;

        /// <summary>
        /// Parameterless constructor for Composer discovery; initialization happens in <see cref="SetJob"/>
        /// </summary>
        public GMOCoinBrokerage() : base("GMOCoin")
        {
        }

        /// <summary>
        /// Creates and initializes a new instance
        /// </summary>
        /// <param name="apiKey">GMO Coin API key (empty for data-only use)</param>
        /// <param name="apiSecret">GMO Coin API secret</param>
        /// <param name="restUrl">Private REST host, e.g. https://api.coin.z.com/private</param>
        /// <param name="publicUrl">Public REST host, e.g. https://api.coin.z.com/public</param>
        /// <param name="webSocketUrl">Public WebSocket url, e.g. wss://api.coin.z.com/ws/public/v1</param>
        /// <param name="privateWebSocketUrl">Private WebSocket base url, e.g. wss://api.coin.z.com/ws/private/v1</param>
        /// <param name="orderProvider">Lean order provider used to resolve orders by brokerage id</param>
        /// <param name="aggregator">Data aggregator for live ticks</param>
        public GMOCoinBrokerage(string apiKey, string apiSecret, string restUrl, string publicUrl,
            string webSocketUrl, string privateWebSocketUrl, IOrderProvider orderProvider, IDataAggregator aggregator)
            : base("GMOCoin")
        {
            Initialize(apiKey, apiSecret, restUrl, publicUrl, webSocketUrl, privateWebSocketUrl, orderProvider, aggregator);
        }

        private void Initialize(string apiKey, string apiSecret, string restUrl, string publicUrl,
            string webSocketUrl, string privateWebSocketUrl, IOrderProvider orderProvider, IDataAggregator aggregator)
        {
            if (_isInitialized)
            {
                return;
            }

            AccountBaseCurrency = Currencies.JPY;

            _restApiClient = new GMOCoinRestApiClient(apiKey, apiSecret, restUrl, publicUrl);
            _orderProvider = orderProvider;
            _aggregator = aggregator;
            _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(GMOCoinMarket.Name);
            _messageHandler = new BrokerageConcurrentMessageHandler<JObject>(ProcessPrivateMessage);

            _publicStreamClient = new GMOCoinPublicWebSocketClient(webSocketUrl);
            _publicStreamClient.MessageReceived += OnStreamMessage;

            if (_restApiClient.HasCredentials)
            {
                _privateStreamClient = new GMOCoinPrivateWebSocketClient(
                    () => _restApiClient.CreateWebSocketToken(),
                    token => _restApiClient.ExtendWebSocketToken(token),
                    privateWebSocketUrl);
                _privateStreamClient.MessageReceived += (_, message) => _messageHandler.HandleNewMessage(message);
            }

            // distinct channel per tick type: Trade and Quote must each trigger SubscribeImpl
            // (the default constructor maps every tick type to a single shared channel)
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager(tickType => tickType.ToString());
            _subscriptionManager.SubscribeImpl = (symbols, tickType) => SubscribeChannels(symbols, tickType);
            _subscriptionManager.UnsubscribeImpl = (symbols, tickType) => UnsubscribeChannels(symbols, tickType);

            _isInitialized = true;
        }

        /// <summary>
        /// True when the public stream is connected and, if credentials were supplied,
        /// the private stream is connected as well
        /// </summary>
        public override bool IsConnected =>
            (_publicStreamClient?.IsConnected ?? false) &&
            (_privateStreamClient == null || _privateStreamClient.IsRunning);

        /// <summary>
        /// Connects the public stream and starts the private stream when credentials are available
        /// </summary>
        public override void Connect()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("GMOCoinBrokerage.Connect(): brokerage is not initialized.");
            }
            if (IsConnected)
            {
                return;
            }

            _publicStreamClient.Connect();
            _privateStreamClient?.Start();
        }

        /// <summary>
        /// Disconnects both streams
        /// </summary>
        public override void Disconnect()
        {
            _privateStreamClient?.Stop();
            _publicStreamClient?.Dispose();
        }

        /// <summary>
        /// Places a new order via POST /v1/order
        /// </summary>
        public override bool PlaceOrder(Order order)
        {
            var submitted = false;
            _messageHandler.WithLockedStream(() =>
            {
                GMOCoinOrderRequest request;
                try
                {
                    request = BuildOrderRequest(order);
                }
                catch (NotSupportedException e)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "GMOCoin Order Event")
                    {
                        Status = OrderStatus.Invalid,
                        Message = e.Message
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported", e.Message));
                    return;
                }

                try
                {
                    var orderId = _restApiClient.CreateOrder(request);
                    order.BrokerId.Add(orderId.ToStringInvariant());
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "GMOCoin Order Event")
                    {
                        Status = OrderStatus.Submitted
                    });
                    submitted = true;
                }
                catch (Exception e)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "GMOCoin Order Event")
                    {
                        Status = OrderStatus.Invalid,
                        Message = e.Message
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "PlaceOrderError", e.Message));
                }
            });
            return submitted;
        }

        /// <summary>
        /// Updates the price of an active limit or stop order via POST /v1/changeOrder.
        /// GMO Coin cannot change the quantity: those updates are rejected by
        /// <see cref="GMOCoinBrokerageModel.CanUpdateOrder"/>.
        /// </summary>
        public override bool UpdateOrder(Order order)
        {
            if (order.BrokerId.Count == 0)
            {
                return false;
            }

            string price;
            switch (order)
            {
                case LimitOrder limit:
                    price = limit.LimitPrice.ToStringInvariant();
                    break;
                case StopMarketOrder stopMarket:
                    price = stopMarket.StopPrice.ToStringInvariant();
                    break;
                default:
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateOrderNotSupported",
                        $"GMO Coin can only update the price of limit and stop orders, not {order.Type} orders."));
                    return false;
            }

            var updated = false;
            _messageHandler.WithLockedStream(() =>
            {
                var orderId = long.Parse(order.BrokerId[0], System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    _restApiClient.ChangeOrder(orderId, price);
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "GMOCoin Order Event")
                    {
                        Status = OrderStatus.UpdateSubmitted
                    });
                    updated = true;
                }
                catch (Exception e)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateOrderError", e.Message));
                }
            });
            return updated;
        }

        /// <summary>
        /// Cancels an order via POST /v1/cancelOrder. The Canceled order event is
        /// emitted when the private stream confirms the cancellation.
        /// </summary>
        public override bool CancelOrder(Order order)
        {
            if (order.BrokerId.Count == 0)
            {
                return false;
            }

            var canceled = false;
            _messageHandler.WithLockedStream(() =>
            {
                var orderId = long.Parse(order.BrokerId[0], System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    _restApiClient.CancelOrder(orderId);
                    canceled = true;
                }
                catch (GMOCoinApiException e) when (e.ErrorCode == "ERR-5122")
                {
                    // already cancelling/canceled/executed/expired: treat as success, the stream event handles state
                    canceled = true;
                }
                catch (Exception e)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelOrderError", e.Message));
                }
            });
            return canceled;
        }

        /// <summary>
        /// Fetches open orders via GET /v1/activeOrders. The endpoint requires a symbol,
        /// so every known GMO Coin spot symbol is queried (rate-gated).
        /// </summary>
        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            foreach (var symbol in GetKnownSymbols())
            {
                var gmoSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                List<GMOCoinOrder> activeOrders;
                try
                {
                    activeOrders = _restApiClient.GetActiveOrders(gmoSymbol);
                }
                catch (Exception e)
                {
                    Log.Error(e, $"GMOCoinBrokerage.GetOpenOrders({gmoSymbol})");
                    continue;
                }

                foreach (var gmoOrder in activeOrders)
                {
                    var order = ConvertOrder(gmoOrder, symbol);
                    if (order == null)
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedOrderType",
                            $"Skipping unsupported GMO Coin order type '{gmoOrder.ExecutionType}' for order {gmoOrder.OrderId}"));
                        continue;
                    }
                    orders.Add(order);
                }
            }
            return orders;
        }

        /// <summary>
        /// GMO Coin spot is a cash account: holdings are represented as cash balances
        /// </summary>
        public override List<Holding> GetAccountHoldings()
        {
            return new List<Holding>();
        }

        /// <summary>
        /// Fetches balances via GET /v1/account/assets, one CashAmount per asset
        /// </summary>
        public override List<CashAmount> GetCashBalance()
        {
            var balances = new List<CashAmount>();
            foreach (var asset in _restApiClient.GetAssets())
            {
                if (asset.Amount != 0)
                {
                    balances.Add(new CashAmount(asset.Amount, asset.Symbol.LazyToUpper()));
                }
            }
            if (!balances.Any(b => b.Currency == Currencies.JPY))
            {
                balances.Add(new CashAmount(0, Currencies.JPY));
            }
            return balances;
        }

        /// <summary>
        /// Maps a Lean order to a GMO Coin create-order request
        /// </summary>
        public GMOCoinOrderRequest BuildOrderRequest(Order order)
        {
            var request = new GMOCoinOrderRequest
            {
                Symbol = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                Size = order.AbsoluteQuantity.ToStringInvariant(),
                Side = order.Direction == OrderDirection.Buy ? "BUY" : "SELL"
            };

            switch (order)
            {
                case MarketOrder:
                    request.ExecutionType = "MARKET";
                    break;

                case LimitOrder limit:
                    request.ExecutionType = "LIMIT";
                    request.Price = limit.LimitPrice.ToStringInvariant();
                    if ((order.Properties as GMOCoinOrderProperties)?.PostOnly == true)
                    {
                        // SOK: the order expires unless it would rest on the book as a maker
                        request.TimeInForce = "SOK";
                    }
                    break;

                case StopMarketOrder stopMarket:
                    request.ExecutionType = "STOP";
                    request.Price = stopMarket.StopPrice.ToStringInvariant();
                    break;

                default:
                    throw new NotSupportedException($"GMOCoinBrokerage: unsupported order type {order.Type}");
            }

            if (order.TimeInForce != Orders.TimeInForce.GoodTilCanceled)
            {
                throw new NotSupportedException("GMOCoinBrokerage: only GoodTilCanceled time in force is supported.");
            }

            return request;
        }

        /// <summary>
        /// Converts a GMO Coin active order to a Lean order, or null for unsupported types.
        /// The remaining quantity is used so restored orders fill the outstanding amount only.
        /// </summary>
        public static Order ConvertOrder(GMOCoinOrder gmoOrder, Symbol symbol)
        {
            var quantity = gmoOrder.Size - gmoOrder.ExecutedSize;
            if (gmoOrder.Side == "SELL")
            {
                quantity = -quantity;
            }
            var time = GMOCoinTime.ToUtc(gmoOrder.Timestamp);

            Order order;
            switch (gmoOrder.ExecutionType)
            {
                case "MARKET":
                    order = new MarketOrder(symbol, quantity, time);
                    break;
                case "LIMIT":
                    order = new LimitOrder(symbol, quantity, gmoOrder.Price ?? 0, time);
                    break;
                case "STOP":
                    order = new StopMarketOrder(symbol, quantity, gmoOrder.Price ?? 0, time);
                    break;
                default:
                    return null;
            }

            order.BrokerId.Add(gmoOrder.OrderId.ToStringInvariant());
            order.Status = gmoOrder.ExecutedSize > 0
                ? OrderStatus.PartiallyFilled
                : OrderStatus.Submitted;
            return order;
        }

        private IEnumerable<Symbol> GetKnownSymbols()
        {
            return SymbolPropertiesDatabase.FromDataFolder()
                .GetSymbolPropertiesList(GMOCoinMarket.Name, SecurityType.Crypto)
                .Select(kvp => Symbol.Create(kvp.Key.Symbol, SecurityType.Crypto, GMOCoinMarket.Name));
        }

        /// <summary>
        /// Disposes clients and streams
        /// </summary>
        public override void Dispose()
        {
            _privateStreamClient.DisposeSafely();
            _publicStreamClient.DisposeSafely();
            _restApiClient.DisposeSafely();
            _messageHandler.DisposeSafely();
            base.Dispose();
        }
    }
}
