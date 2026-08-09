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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.GMOCoin.Streaming
{
    /// <summary>
    /// GMO Coin private WebSocket client. Order/execution events are pushed over
    /// wss://api.coin.z.com/ws/private/v1/{accessToken}; the token comes from the
    /// signed POST /v1/ws-auth endpoint, is valid for 60 minutes and is kept alive
    /// by a periodic PUT /v1/ws-auth extension. If the extension fails, a fresh
    /// token is fetched and the socket reconnects. Channel subscriptions
    /// (executionEvents, orderEvents) are throttled to GMO Coin's 1 command/second
    /// limit and replayed on every reconnect.
    /// See https://api.coin.z.com/docs/#ws-private
    /// </summary>
    public class GMOCoinPrivateWebSocketClient : IDisposable
    {
        /// <summary>
        /// Channels subscribed on every (re)connect
        /// </summary>
        public static readonly string[] Channels = { "executionEvents", "orderEvents" };

        private static readonly TimeSpan TokenExtendInterval = TimeSpan.FromMinutes(25);

        private readonly Func<string> _tokenProvider;
        private readonly Action<string> _tokenExtender;
        private readonly string _baseUrl;
        private readonly object _locker = new();
        private WebSocketClientWrapper _webSocket;
        private Timer _extendTimer;
        private string _token;
        private volatile bool _isRunning;

        /// <summary>
        /// Fired for each private channel message (parsed JSON object with a "channel" field)
        /// </summary>
        public event EventHandler<JObject> MessageReceived;

        /// <summary>
        /// True while the client is started and the websocket is open
        /// </summary>
        public bool IsRunning => _isRunning && (_webSocket?.IsOpen ?? false);

        /// <summary>
        /// Creates a new private stream client
        /// </summary>
        /// <param name="tokenProvider">Returns a fresh access token, normally by calling POST /v1/ws-auth</param>
        /// <param name="tokenExtender">Extends an access token, normally by calling PUT /v1/ws-auth</param>
        /// <param name="baseUrl">Private WebSocket base url, e.g. wss://api.coin.z.com/ws/private/v1</param>
        public GMOCoinPrivateWebSocketClient(Func<string> tokenProvider, Action<string> tokenExtender, string baseUrl)
        {
            _tokenProvider = tokenProvider;
            _tokenExtender = tokenExtender;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        /// <summary>
        /// Fetches an access token, connects the websocket and starts the token keep-alive timer
        /// </summary>
        public void Start()
        {
            lock (_locker)
            {
                if (_isRunning)
                {
                    return;
                }
                _isRunning = true;
                ConnectWithToken(_tokenProvider());
                _extendTimer = new Timer(_ => ExtendToken(), null, TokenExtendInterval, TokenExtendInterval);
            }
        }

        /// <summary>
        /// Stops the client and closes the websocket
        /// </summary>
        public void Stop()
        {
            lock (_locker)
            {
                _isRunning = false;
                _extendTimer.DisposeSafely();
                _extendTimer = null;
                _webSocket?.Close();
                _webSocket = null;
            }
        }

        private void ConnectWithToken(string token)
        {
            _token = token;
            var webSocket = new WebSocketClientWrapper();
            webSocket.Initialize($"{_baseUrl}/{token}");
            webSocket.Message += (_, e) => OnFrame((e.Data as WebSocketClientWrapper.TextMessage)?.Message);
            webSocket.Open += (_, _) =>
            {
                Log.Trace("GMOCoinPrivateWebSocketClient: connected, subscribing private channels");
                // subscribe commands are limited to 1/second per IP
                Task.Run(() =>
                {
                    foreach (var channel in Channels)
                    {
                        try
                        {
                            webSocket.Send(BuildCommand("subscribe", channel));
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, $"GMOCoinPrivateWebSocketClient: failed to subscribe {channel}");
                        }
                        Thread.Sleep(1100);
                    }
                });
            };
            _webSocket = webSocket;
            webSocket.Connect();
        }

        private void ExtendToken()
        {
            var token = _token;
            if (!_isRunning || token == null)
            {
                return;
            }

            try
            {
                _tokenExtender(token);
                Log.Trace("GMOCoinPrivateWebSocketClient: access token extended");
            }
            catch (Exception extendError)
            {
                Log.Error(extendError, "GMOCoinPrivateWebSocketClient.ExtendToken(): extension failed, fetching a new token");
                try
                {
                    lock (_locker)
                    {
                        if (!_isRunning)
                        {
                            return;
                        }
                        _webSocket?.Close();
                        ConnectWithToken(_tokenProvider());
                    }
                }
                catch (Exception refreshError)
                {
                    // keep the timer alive: the next tick retries the refresh
                    Log.Error(refreshError, "GMOCoinPrivateWebSocketClient.ExtendToken(): token refresh failed");
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
                    Log.Error($"GMOCoinPrivateWebSocketClient: server error: {json["error"]}");
                    return;
                }
                if (json["channel"] != null)
                {
                    MessageReceived?.Invoke(this, json);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"GMOCoinPrivateWebSocketClient.OnFrame(): failed to process frame: {frame}");
            }
        }

        /// <summary>
        /// Builds a subscribe/unsubscribe command frame for a private channel
        /// </summary>
        public static string BuildCommand(string command, string channel)
        {
            return new JObject
            {
                ["command"] = command,
                ["channel"] = channel
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Stops the client and disposes resources
        /// </summary>
        public void Dispose()
        {
            Stop();
        }
    }
}
