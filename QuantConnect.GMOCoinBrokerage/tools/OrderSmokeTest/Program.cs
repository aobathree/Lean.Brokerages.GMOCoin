// Minimum-lot order lifecycle smoke test (integration test phase P3):
//   1. subscribe to the private WebSocket (orderEvents)
//   2. place a post-only (SOK) limit BUY 10% below the market (min lot 0.00001 BTC;
//      GMO Coin rejects prices further from the market with ERR-5121, and SOK
//      guarantees the order can only rest on the book, never execute as a taker)
//   3. wait for the ORDERED orderEvents message
//   4. cancel the order
//   5. wait for the CANCELED orderEvents message
//
// This PLACES A REAL ORDER on your GMO Coin account (canceled immediately, and priced
// 10% below market with SOK post-only so it cannot execute as a taker). Run it yourself, deliberately:
//   op run --env-file=.env.1password -- dotnet run --project tools/OrderSmokeTest -- --yes
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using QuantConnect.Brokerages.GMOCoin.Api;
using QuantConnect.Brokerages.GMOCoin.Messages;
using QuantConnect.Brokerages.GMOCoin.Streaming;

const string GMOSymbol = "BTC";
const decimal Amount = 0.00001m; // GMO Coin minimum order size for spot BTC

var apiKey = Environment.GetEnvironmentVariable("GMOCOIN_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("GMOCOIN_API_SECRET");

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
{
    Console.Error.WriteLine("ERROR: GMOCOIN_API_KEY / GMOCOIN_API_SECRET are not set.");
    return 1;
}
if (!args.Contains("--yes"))
{
    Console.Error.WriteLine("This test places (and immediately cancels) a REAL minimum-lot order on your account.");
    Console.Error.WriteLine("Re-run with --yes to proceed:");
    Console.Error.WriteLine("  op run --env-file=.env.1password -- dotnet run --project tools/OrderSmokeTest -- --yes");
    return 1;
}

using var restClient = new GMOCoinRestApiClient(apiKey, apiSecret,
    "https://api.coin.z.com/private", "https://api.coin.z.com/public");

// current best bid from the public ticker; order priced at 90% of it (tick size 1 JPY).
// GMO Coin rejects limit orders too far from the market (ERR-5121), so unlike the
// bitbank sibling test we cannot use 50%; -10% stays inside the allowed band while
// SOK (post-only) still guarantees the order cannot execute as a taker.
var ticker = restClient.GetTicker(GMOSymbol);
var bestBid = decimal.Parse(ticker["bid"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
var orderPrice = Math.Floor(bestBid * 0.9m);

Console.WriteLine("=== GMO Coin order lifecycle smoke test ===");
Console.WriteLine($"  symbol      : {GMOSymbol}");
Console.WriteLine($"  side/type   : BUY / LIMIT (timeInForce=SOK, post-only)");
Console.WriteLine($"  size        : {Amount} BTC (minimum lot)");
Console.WriteLine($"  price       : {orderPrice} JPY (best bid {bestBid} x 0.9 — resting maker order, will not fill)");
Console.WriteLine($"  max exposure: ~{Math.Ceiling(Amount * orderPrice)} JPY if it somehow filled");
Console.WriteLine();
Console.Write("Press Enter to place the order, Ctrl+C to abort... ");
Console.ReadLine();

// 1) subscribe to the private WebSocket and record order events per order id
var events = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
using var stream = new GMOCoinPrivateWebSocketClient(
    () => restClient.CreateWebSocketToken(),
    token => restClient.ExtendWebSocketToken(token),
    "wss://api.coin.z.com/ws/private/v1");
stream.MessageReceived += (_, message) =>
{
    if (message["channel"]?.ToString() == "orderEvents")
    {
        var orderId = message["orderId"]?.ToString();
        var status = message["orderStatus"]?.ToString();
        var queue = events.GetOrAdd(orderId ?? string.Empty, _ => new ConcurrentQueue<string>());
        queue.Enqueue(status ?? string.Empty);
        Console.WriteLine($"  [stream] msgType={message["msgType"]} order={orderId} status={status}");
    }
};
stream.Start();
Thread.Sleep(3000); // allow the connection and channel subscriptions to establish

// 2) place the order
var placedOrderId = restClient.CreateOrder(new GMOCoinOrderRequest
{
    Symbol = GMOSymbol,
    Size = Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
    Price = orderPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
    Side = "BUY",
    ExecutionType = "LIMIT",
    TimeInForce = "SOK"
});
Console.WriteLine($"placed: orderId={placedOrderId}");
var orderId = placedOrderId.ToString(System.Globalization.CultureInfo.InvariantCulture);

// 3) wait for the stream to confirm the new order
var confirmed = WaitFor(() => events.TryGetValue(orderId, out var q) && q.Any(e => e == "ORDERED" || e == "WAITING"),
    TimeSpan.FromSeconds(15));
Console.WriteLine(confirmed
    ? "stream confirmed the new order (orderEvents)"
    : "WARN: no stream event within 15s (order state will be verified via REST)");

// 4) cancel
restClient.CancelOrder(placedOrderId);
Console.WriteLine("cancel requested");

// 5) wait for the cancellation event
var cancelSeen = WaitFor(() => events.TryGetValue(orderId, out var q) && q.Any(e => e == "CANCELED"),
    TimeSpan.FromSeconds(15));

// final REST verification (CANCELLING can take a moment to settle)
var final = restClient.GetOrder(placedOrderId);
for (var i = 0; i < 10 && final != null && final.Status == GMOCoinOrderStatus.Cancelling; i++)
{
    Thread.Sleep(1000);
    final = restClient.GetOrder(placedOrderId);
}
Console.WriteLine($"final order status via REST: {final?.Status}");

var streamOk = confirmed && cancelSeen;
var restOk = final?.Status == GMOCoinOrderStatus.Canceled;
Console.WriteLine();
Console.WriteLine($"RESULT: order lifecycle {(restOk ? "OK" : "FAILED")}, private stream events {(streamOk ? "OK" : "MISSING")}");
return restOk ? 0 : 1;

static bool WaitFor(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return true;
        }
        Thread.Sleep(200);
    }
    return condition();
}
