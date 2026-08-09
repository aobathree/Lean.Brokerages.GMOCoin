// Downloads GMO Coin public kline data and writes it in Lean's crypto
// data format (Data/crypto/gmocoin/{daily|hour}/{symbol}_trade.zip).
//
// Usage:
//   dotnet run --project tools/CandleDownloader -- \
//     --symbols BTC,ETH --resolution daily --from 2018 --data-dir Data
//
//   --resolution daily : fetches 1day klines (one request per year)
//   --resolution hour  : fetches 1hour klines (one request per day; slow)
//   --from             : first year (daily) or first date yyyyMMdd (hour, >= 20210415)
//
// Public API only; no credentials required. Existing zips are overwritten.
// Note: GMO Coin kline days run 06:00 JST to 05:59 JST; bars are written with
// their exact UTC open time.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

var symbols = new List<string> { "BTC", "ETH", "XRP", "SOL", "DOGE", "XLM", "ADA", "LTC" };
var resolution = "daily";
var from = "2018";
var dataDir = "Data";   // relative to the Lean repo root

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--symbols": symbols = args[i + 1].Split(',').Select(p => p.Trim().ToUpperInvariant()).ToList(); break;
        case "--resolution": resolution = args[i + 1]; break;
        case "--from": from = args[i + 1]; break;
        case "--data-dir": dataDir = args[i + 1]; break;
    }
}

if (resolution != "daily" && resolution != "hour")
{
    Console.Error.WriteLine("ERROR: --resolution must be 'daily' or 'hour'");
    return 1;
}

var outputDir = Path.Combine(dataDir, "crypto", "gmocoin", resolution);
Directory.CreateDirectory(outputDir);

// trailing slash matters: a base address path segment is dropped for "/x" relative urls
using var http = new HttpClient { BaseAddress = new Uri("https://api.coin.z.com/public/") };
http.Timeout = TimeSpan.FromSeconds(30);

var utcToday = DateTime.UtcNow.Date;

foreach (var gmoSymbol in symbols)
{
    var fileSymbol = gmoSymbol.ToLowerInvariant() + "jpy";   // BTC -> btcjpy (Lean file symbol)
    var lines = new SortedDictionary<long, string>();
    var interval = resolution == "daily" ? "1day" : "1hour";
    var requests = BuildRequestDates(resolution, from, utcToday);
    var missing = 0;

    foreach (var date in requests)
    {
        var url = $"v1/klines?symbol={gmoSymbol}&interval={interval}&date={date}";
        JsonDocument doc;
        try
        {
            var body = await FetchWithRetryAsync(http, url);
            if (body == null) { missing++; continue; }
            doc = JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {gmoSymbol} {date}: {ex.Message} (skipped)");
            missing++;
            continue;
        }

        using (doc)
        {
            if (doc.RootElement.GetProperty("status").GetInt32() != 0) { missing++; continue; }
            if (!doc.RootElement.TryGetProperty("data", out var candles) ||
                candles.ValueKind != JsonValueKind.Array || candles.GetArrayLength() == 0)
            {
                missing++;
                continue;
            }

            foreach (var row in candles.EnumerateArray())
            {
                // {"openTime":"ms","open":...,"high":...,"low":...,"close":...,"volume":...} (numbers arrive as strings)
                var ts = long.Parse(row.GetProperty("openTime").GetString()!, CultureInfo.InvariantCulture);
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
                if (t >= utcToday) continue;   // drop today's incomplete bar
                var line = string.Join(",",
                    t.ToString("yyyyMMdd HH:mm", CultureInfo.InvariantCulture),
                    Num(row.GetProperty("open")), Num(row.GetProperty("high")),
                    Num(row.GetProperty("low")), Num(row.GetProperty("close")),
                    Num(row.GetProperty("volume")));
                lines[ts] = line;
            }
        }
        // stay well under public API tolerance
        await Task.Delay(resolution == "daily" ? 150 : 120);
    }

    if (lines.Count == 0)
    {
        Console.WriteLine($"{gmoSymbol}: no data (symbol may not have existed yet) — skipped");
        continue;
    }

    var zipPath = Path.Combine(outputDir, $"{fileSymbol}_trade.zip");
    File.Delete(zipPath);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry($"{fileSymbol}.csv");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        foreach (var line in lines.Values) writer.WriteLine(line);
    }

    var first = DateTimeOffset.FromUnixTimeMilliseconds(lines.Keys.First()).UtcDateTime;
    var last = DateTimeOffset.FromUnixTimeMilliseconds(lines.Keys.Last()).UtcDateTime;
    Console.WriteLine($"{gmoSymbol}: {lines.Count} bars {first:yyyy-MM-dd} .. {last:yyyy-MM-dd} -> {zipPath}" +
                      (missing > 0 ? $" ({missing} empty periods)" : ""));
}

return 0;

static IEnumerable<string> BuildRequestDates(string resolution, string from, DateTime utcToday)
{
    if (resolution == "daily")
    {
        for (var y = int.Parse(from); y <= utcToday.Year; y++) yield return y.ToString();
    }
    else
    {
        var min = new DateTime(2021, 4, 15);   // first day available from the intraday kline endpoint
        var d = DateTime.ParseExact(from, "yyyyMMdd", CultureInfo.InvariantCulture);
        if (d < min) d = min;
        for (; d <= utcToday; d = d.AddDays(1)) yield return d.ToString("yyyyMMdd");
    }
}

static async Task<string?> FetchWithRetryAsync(HttpClient http, string url)
{
    for (var attempt = 1; ; attempt++)
    {
        var response = await http.GetAsync(url);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode == 404) return null;
        if (attempt >= 3) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        await Task.Delay(1000 * attempt * attempt);
    }
}

static string Num(JsonElement e) => e.ValueKind == JsonValueKind.String
    ? e.GetString()!
    : e.GetRawText();
