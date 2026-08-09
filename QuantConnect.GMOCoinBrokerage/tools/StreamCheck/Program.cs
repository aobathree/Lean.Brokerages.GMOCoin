// Private WebSocket reception check: subscribes to executionEvents / orderEvents
// and prints every message for a fixed duration. Read-only, no orders.
// Run via: op run --env-file=.env.1password -- dotnet run --project tools/StreamCheck [seconds]
using System;
using System.Threading;
using QuantConnect.Brokerages.GMOCoin.Api;
using QuantConnect.Brokerages.GMOCoin.Streaming;
using QuantConnect.Logging;

var apiKey = Environment.GetEnvironmentVariable("GMOCOIN_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("GMOCOIN_API_SECRET");
var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
{
    Console.Error.WriteLine("ERROR: GMOCOIN_API_KEY / GMOCOIN_API_SECRET are not set.");
    Console.Error.WriteLine("Run via: op run --env-file=.env.1password -- dotnet run --project tools/StreamCheck");
    return 1;
}

// surface the connector's internal trace logs (token fetch, reconnects) on the console
Log.LogHandler = new ConsoleLogHandler();

using var restClient = new GMOCoinRestApiClient(apiKey, apiSecret,
    "https://api.coin.z.com/private", "https://api.coin.z.com/public");

var messageCount = 0;
using var stream = new GMOCoinPrivateWebSocketClient(
    () => restClient.CreateWebSocketToken(),
    token => restClient.ExtendWebSocketToken(token),
    "wss://api.coin.z.com/ws/private/v1");
stream.MessageReceived += (_, message) =>
{
    Interlocked.Increment(ref messageCount);
    Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] channel={message["channel"]} {message.ToString(Newtonsoft.Json.Formatting.None)}");
};

Console.WriteLine($"Subscribing to the private WebSocket for {durationSeconds}s...");
Console.WriteLine("Tip: while this runs, any account activity (placing/canceling an order from the");
Console.WriteLine("GMO Coin app, fills, etc.) will appear here as orderEvents / executionEvents.");
stream.Start();

var deadline = DateTime.UtcNow.AddSeconds(durationSeconds);
var graceDeadline = DateTime.UtcNow.AddSeconds(15);
while (DateTime.UtcNow < deadline)
{
    Thread.Sleep(500);
    if (!stream.IsRunning && DateTime.UtcNow > graceDeadline)
    {
        Console.Error.WriteLine("FAILED: private WebSocket is not connected.");
        return 1;
    }
}

Console.WriteLine($"OK: stream stayed connected for {durationSeconds}s, received {messageCount} message(s).");
Console.WriteLine("(0 messages is normal when there was no account activity during the window.)");
return 0;
