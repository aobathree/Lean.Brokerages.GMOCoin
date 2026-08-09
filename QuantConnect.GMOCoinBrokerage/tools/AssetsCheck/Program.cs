// One-shot GMO Coin private API connectivity check: fetches /v1/account/assets,
// active BTC orders and a private WebSocket token using credentials from
// GMOCOIN_API_KEY / GMOCOIN_API_SECRET.
// Run via: op run --env-file=.env.1password -- dotnet run --project tools/AssetsCheck
using System;
using QuantConnect.Brokerages.GMOCoin.Api;

var apiKey = Environment.GetEnvironmentVariable("GMOCOIN_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("GMOCOIN_API_SECRET");

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
{
    Console.Error.WriteLine("ERROR: GMOCOIN_API_KEY / GMOCOIN_API_SECRET are not set.");
    Console.Error.WriteLine("Run via: op run --env-file=.env.1password -- dotnet run --project tools/AssetsCheck");
    return 1;
}

using var client = new GMOCoinRestApiClient(apiKey, apiSecret,
    "https://api.coin.z.com/private", "https://api.coin.z.com/public");

try
{
    Console.WriteLine("== GET /v1/account/assets ==");
    foreach (var asset in client.GetAssets())
    {
        if (asset.Amount != 0 || asset.Symbol is "JPY" or "BTC")
        {
            Console.WriteLine($"  {asset.Symbol,-8} amount={asset.Amount} available={asset.Available}");
        }
    }

    Console.WriteLine("== GET /v1/activeOrders?symbol=BTC ==");
    var orders = client.GetActiveOrders("BTC");
    Console.WriteLine($"  active BTC orders: {orders.Count}");
    foreach (var order in orders)
    {
        Console.WriteLine($"  #{order.OrderId} {order.Symbol} {order.Side} {order.ExecutionType} " +
            $"remaining={order.Size - order.ExecutedSize} price={order.Price} status={order.Status}");
    }

    Console.WriteLine("== POST /v1/ws-auth (private WebSocket token) ==");
    var token = client.CreateWebSocketToken();
    Console.WriteLine($"  token={token[..Math.Min(8, token.Length)]}... (len={token.Length})");

    Console.WriteLine("OK: private API connectivity verified.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAILED: {e.Message}");
    return 1;
}
