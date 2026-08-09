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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin.Streaming
{
    /// <summary>
    /// GMO Coin public WebSocket client (wss://api.coin.z.com/ws/public/v1).
    /// Channels are plain JSON subscribe/unsubscribe commands; GMO Coin limits
    /// subscribe requests to one per second per IP, so commands are drained from
    /// a queue by a throttled sender task. Subscriptions are tracked and replayed
    /// automatically after every reconnect.
    /// See https://api.coin.z.com/docs/#ws-public
    /// </summary>
    public class GMOCoinPublicWebSocketClient : IDisposable
    {
        /// <summary>
        /// Delay between outgoing subscribe/unsubscribe commands, kept above
        /// GMO Coin's 1 request/second limit
        /// </summary>
        public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(1100);

        private readonly WebSocketClientWrapper _webSocket = new();
        private readonly ConcurrentDictionary<(string Channel, string Symbol), byte> _subscriptions = new();
        private readonly ConcurrentQueue<string> _sendQueue = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _senderTask;

        /// <summary>
        /// Fired for each received channel message (parsed JSON object with a "channel" field)
        /// </summary>
        public event EventHandler<JObject> MessageReceived;

        /// <summary>
        /// True when the websocket is open
        /// </summary>
        public bool IsConnected => _webSocket.IsOpen;

        /// <summary>
        /// Creates a client for the given url, e.g. wss://api.coin.z.com/ws/public/v1
        /// </summary>
        public GMOCoinPublicWebSocketClient(string url)
        {
            _webSocket.Initialize(url);
            _webSocket.Message += (_, e) => OnFrame((e.Data as WebSocketClientWrapper.TextMessage)?.Message);
            _webSocket.Open += (_, _) =>
            {
                Log.Trace($"GMOCoinPublicWebSocketClient: connected, re-subscribing {_subscriptions.Count} channel(s)");
                foreach (var (channel, symbol) in _subscriptions.Keys)
                {
                    _sendQueue.Enqueue(BuildCommand("subscribe", channel, symbol));
                }
            };
        }

        /// <summary>
        /// Connects the websocket and starts the throttled command sender
        /// </summary>
        public void Connect()
        {
            _senderTask ??= Task.Factory.StartNew(() => SenderLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _webSocket.Connect();
        }

        /// <summary>
        /// Subscribes to a channel (e.g. trades/BTC); re-subscribed automatically on reconnect
        /// </summary>
        public void Subscribe(string channel, string symbol)
        {
            if (_subscriptions.TryAdd((channel, symbol), 0) && IsConnected)
            {
                _sendQueue.Enqueue(BuildCommand("subscribe", channel, symbol));
            }
        }

        /// <summary>
        /// Unsubscribes from a channel
        /// </summary>
        public void Unsubscribe(string channel, string symbol)
        {
            if (_subscriptions.TryRemove((channel, symbol), out _) && IsConnected)
            {
                _sendQueue.Enqueue(BuildCommand("unsubscribe", channel, symbol));
            }
        }

        private void SenderLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (IsConnected && _sendQueue.TryDequeue(out var command))
                {
                    try
                    {
                        _webSocket.Send(command);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"GMOCoinPublicWebSocketClient.SenderLoop(): failed to send {command}");
                    }
                    // stay under the 1 subscribe/second limit
                    if (token.WaitHandle.WaitOne(SendInterval))
                    {
                        break;
                    }
                }
                else if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    break;
                }
            }
        }

        private void OnFrame(string frame)
        {
            if (string.IsNullOrEmpty(frame))
            {
                return;
            }

            try
            {
                var json = JObject.Parse(frame);
                if (json["error"] != null)
                {
                    Log.Error($"GMOCoinPublicWebSocketClient: server error: {json["error"]}");
                    return;
                }
                if (json["channel"] != null)
                {
                    MessageReceived?.Invoke(this, json);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"GMOCoinPublicWebSocketClient.OnFrame(): failed to process frame: {frame}");
            }
        }

        /// <summary>
        /// Builds a subscribe/unsubscribe command frame
        /// </summary>
        public static string BuildCommand(string command, string channel, string symbol)
        {
            return new JObject
            {
                ["command"] = command,
                ["channel"] = channel,
                ["symbol"] = symbol
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Closes the websocket and stops the sender
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _webSocket.Close();
            try
            {
                _senderTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // ignored: cancellation surfaces as AggregateException
            }
            _cts.DisposeSafely();
        }
    }
}
