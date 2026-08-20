using System.Data;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CryptoBotWeb.Infrastructure.Simulation;

/// <summary>
/// DB-backed cache of historical klines for the simulator. Closed candles never change, so a
/// window downloaded once is served from Postgres forever after; only uncovered gaps hit the
/// exchange. Coverage is tracked per (exchange, symbol, timeframe) as a list of half-open
/// [from,to) windows in <c>market_candle_ranges</c> — a missing candle inside a covered window
/// means the exchange has none for that minute, not that we need to fetch it again.
///
/// The newest few minutes are never cached (the last candle may still be forming): that tail is
/// fetched live on every run. Writes go through COPY into a temp table + INSERT … ON CONFLICT DO
/// NOTHING, so a year of 1m bars (~525k rows) lands in a few seconds.
/// </summary>
public class KlineHistoryCache
{
    /// <summary>Candles younger than this are re-fetched live and not stored (may be incomplete).</summary>
    private static readonly TimeSpan LiveTail = TimeSpan.FromMinutes(3);

    private readonly AppDbContext _db;
    private readonly ILogger<KlineHistoryCache> _logger;

    public KlineHistoryCache(AppDbContext db, ILogger<KlineHistoryCache> logger)
    {
        _db = db;
        _logger = logger;
    }

    public sealed class FetchStats
    {
        public int FromCache;
        public int Downloaded;
        public int GapsFilled;
        public double DownloadSeconds;
        public double CacheReadSeconds;
    }

    /// <summary>
    /// Returns candles for [fromUtc, toUtc) ascending, serving covered windows from the DB and
    /// downloading the rest through <paramref name="download"/> (which must return ascending
    /// candles for the requested sub-window, throwing on failure).
    /// </summary>
    public async Task<List<CandleDto>> GetOrDownloadAsync(
        ExchangeType exchange, string symbol, string timeframe, DateTime fromUtc, DateTime toUtc,
        Func<DateTime, DateTime, CancellationToken, Task<List<CandleDto>>> download,
        FetchStats? stats, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        timeframe = timeframe.Trim().ToLowerInvariant();
        fromUtc = FloorMinute(fromUtc);
        toUtc = FloorMinute(toUtc);
        if (toUtc <= fromUtc) return new List<CandleDto>();

        // Nothing newer than (now - LiveTail) is cached; that tail is fetched live below.
        var cacheTo = FloorMinute(DateTime.UtcNow - LiveTail);
        if (cacheTo > toUtc) cacheTo = toUtc;

        var merged = new SortedDictionary<DateTime, CandleDto>();

        if (cacheTo > fromUtc)
        {
            var covered = await _db.MarketCandleRanges.AsNoTracking()
                .Where(r => r.ExchangeType == exchange && r.Symbol == symbol && r.Timeframe == timeframe
                            && r.ToUtc > fromUtc && r.FromUtc < cacheTo)
                .OrderBy(r => r.FromUtc)
                .Select(r => new { r.FromUtc, r.ToUtc })
                .ToListAsync(ct);

            // Gaps = [fromUtc, cacheTo) minus the union of covered windows.
            var gaps = new List<(DateTime From, DateTime To)>();
            var cursor = fromUtc;
            foreach (var r in covered)
            {
                if (r.FromUtc > cursor) gaps.Add((cursor, Min(r.FromUtc, cacheTo)));
                if (r.ToUtc > cursor) cursor = r.ToUtc;
                if (cursor >= cacheTo) break;
            }
            if (cursor < cacheTo) gaps.Add((cursor, cacheTo));

            foreach (var gap in gaps)
            {
                ct.ThrowIfCancellationRequested();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fresh = await download(gap.From, gap.To, ct);
                sw.Stop();
                if (stats != null) { stats.Downloaded += fresh.Count; stats.GapsFilled++; stats.DownloadSeconds += sw.Elapsed.TotalSeconds; }

                await StoreAsync(exchange, symbol, timeframe, gap.From, gap.To, fresh, ct);
                _logger.LogInformation("Kline cache: {Exchange} {Symbol} {Tf} filled gap [{From:u} .. {To:u}) with {Count} candles in {Sec:0.0}s",
                    exchange, symbol, timeframe, gap.From, gap.To, fresh.Count, sw.Elapsed.TotalSeconds);

                foreach (var c in fresh)
                    if (c.OpenTime >= fromUtc && c.OpenTime < toUtc) merged[c.OpenTime] = c;
            }

            // Everything in [fromUtc, cacheTo) is now covered — read it back in one pass (what was
            // just stored is identical to the in-memory copies above; TryAdd keeps the first).
            var swRead = System.Diagnostics.Stopwatch.StartNew();
            var cached = await ReadAsync(exchange, symbol, timeframe, fromUtc, cacheTo, ct);
            swRead.Stop();
            int fromCache = 0;
            foreach (var c in cached)
                if (merged.TryAdd(c.OpenTime, c)) fromCache++;
            if (stats != null) { stats.FromCache += fromCache; stats.CacheReadSeconds += swRead.Elapsed.TotalSeconds; }
        }

        // Live tail (recent minutes), never cached.
        var tailFrom = Max(fromUtc, cacheTo);
        if (toUtc > tailFrom)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tail = await download(tailFrom, toUtc, ct);
            sw.Stop();
            if (stats != null) { stats.Downloaded += tail.Count; stats.DownloadSeconds += sw.Elapsed.TotalSeconds; }
            foreach (var c in tail)
                if (c.OpenTime >= fromUtc && c.OpenTime < toUtc) merged[c.OpenTime] = c;
        }

        return merged.Values.ToList();
    }

    // ───────────────────────── storage ─────────────────────────

    private async Task StoreAsync(ExchangeType exchange, string symbol, string timeframe,
        DateTime gapFrom, DateTime gapTo, List<CandleDto> candles, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);

            if (candles.Count > 0)
            {
                await using (var cmd = new NpgsqlCommand(
                    "CREATE TEMP TABLE tmp_market_candles (LIKE market_candles) ON COMMIT DROP", conn, tx))
                    await cmd.ExecuteNonQueryAsync(ct);

                await using (var writer = await conn.BeginBinaryImportAsync(
                    "COPY tmp_market_candles (\"ExchangeType\", \"Symbol\", \"Timeframe\", \"OpenTime\", \"Open\", \"High\", \"Low\", \"Close\", \"Volume\") FROM STDIN (FORMAT BINARY)", ct))
                {
                    var ex = (int)exchange;
                    foreach (var c in candles)
                    {
                        if (c.OpenTime < gapFrom || c.OpenTime >= gapTo) continue; // only what this gap covers
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(ex, NpgsqlDbType.Integer, ct);
                        await writer.WriteAsync(symbol, NpgsqlDbType.Varchar, ct);
                        await writer.WriteAsync(timeframe, NpgsqlDbType.Varchar, ct);
                        await writer.WriteAsync(DateTime.SpecifyKind(c.OpenTime, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, ct);
                        await writer.WriteAsync(c.Open, NpgsqlDbType.Numeric, ct);
                        await writer.WriteAsync(c.High, NpgsqlDbType.Numeric, ct);
                        await writer.WriteAsync(c.Low, NpgsqlDbType.Numeric, ct);
                        await writer.WriteAsync(c.Close, NpgsqlDbType.Numeric, ct);
                        await writer.WriteAsync(c.Volume, NpgsqlDbType.Numeric, ct);
                    }
                    await writer.CompleteAsync(ct);
                }

                await using (var cmd = new NpgsqlCommand(
                    "INSERT INTO market_candles SELECT * FROM tmp_market_candles ON CONFLICT DO NOTHING", conn, tx))
                    await cmd.ExecuteNonQueryAsync(ct);
            }

            // Coverage bookkeeping: add the new window, then coalesce everything for this key.
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO market_candle_ranges (\"ExchangeType\", \"Symbol\", \"Timeframe\", \"FromUtc\", \"ToUtc\", \"DownloadedAt\") " +
                "VALUES (@ex, @sym, @tf, @from, @to, @now)", conn, tx))
            {
                cmd.Parameters.AddWithValue("ex", (int)exchange);
                cmd.Parameters.AddWithValue("sym", symbol);
                cmd.Parameters.AddWithValue("tf", timeframe);
                cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(gapFrom, DateTimeKind.Utc));
                cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(gapTo, DateTimeKind.Utc));
                cmd.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await CoalesceRangesAsync(conn, tx, exchange, symbol, timeframe, ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }

    /// <summary>Merge overlapping/adjacent coverage windows for one key into the minimal set.</summary>
    private static async Task CoalesceRangesAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        ExchangeType exchange, string symbol, string timeframe, CancellationToken ct)
    {
        var rows = new List<(Guid Id, DateTime From, DateTime To)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT \"Id\", \"FromUtc\", \"ToUtc\" FROM market_candle_ranges " +
            "WHERE \"ExchangeType\" = @ex AND \"Symbol\" = @sym AND \"Timeframe\" = @tf ORDER BY \"FromUtc\" FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("ex", (int)exchange);
            cmd.Parameters.AddWithValue("sym", symbol);
            cmd.Parameters.AddWithValue("tf", timeframe);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add((rd.GetGuid(0), rd.GetDateTime(1), rd.GetDateTime(2)));
        }
        if (rows.Count < 2) return;

        var keep = new List<(Guid Id, DateTime From, DateTime To)>();
        var cur = rows[0];
        var deleteIds = new List<Guid>();
        foreach (var r in rows.Skip(1))
        {
            if (r.From <= cur.To) // overlap or touch → absorb
            {
                if (r.To > cur.To) cur.To = r.To;
                deleteIds.Add(r.Id);
            }
            else
            {
                keep.Add(cur);
                cur = r;
            }
        }
        keep.Add(cur);

        foreach (var k in keep)
        {
            await using var upd = new NpgsqlCommand(
                "UPDATE market_candle_ranges SET \"FromUtc\" = @from, \"ToUtc\" = @to WHERE \"Id\" = @id", conn, tx);
            upd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(k.From, DateTimeKind.Utc));
            upd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(k.To, DateTimeKind.Utc));
            upd.Parameters.AddWithValue("id", k.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        if (deleteIds.Count > 0)
        {
            await using var del = new NpgsqlCommand("DELETE FROM market_candle_ranges WHERE \"Id\" = ANY(@ids)", conn, tx);
            del.Parameters.AddWithValue("ids", deleteIds.ToArray());
            await del.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<List<CandleDto>> ReadAsync(ExchangeType exchange, string symbol, string timeframe,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var span = SymbolHelper.GetTimeframeSpan(timeframe);
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT \"OpenTime\", \"Open\", \"High\", \"Low\", \"Close\", \"Volume\" FROM market_candles " +
                "WHERE \"ExchangeType\" = @ex AND \"Symbol\" = @sym AND \"Timeframe\" = @tf AND \"OpenTime\" >= @from AND \"OpenTime\" < @to " +
                "ORDER BY \"OpenTime\"", conn);
            cmd.Parameters.AddWithValue("ex", (int)exchange);
            cmd.Parameters.AddWithValue("sym", symbol);
            cmd.Parameters.AddWithValue("tf", timeframe);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(toUtc, DateTimeKind.Utc));

            var expected = (toUtc - fromUtc).Ticks / span.Ticks + 1;
            var list = new List<CandleDto>((int)Math.Min(int.MaxValue, expected));
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var open = DateTime.SpecifyKind(rd.GetDateTime(0), DateTimeKind.Utc);
                list.Add(new CandleDto
                {
                    OpenTime = open,
                    CloseTime = open + span,
                    Open = rd.GetDecimal(1),
                    High = rd.GetDecimal(2),
                    Low = rd.GetDecimal(3),
                    Close = rd.GetDecimal(4),
                    Volume = rd.GetDecimal(5)
                });
            }
            return list;
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }

    // ───────────────────────── admin / maintenance ─────────────────────────

    public sealed class CacheEntry
    {
        public ExchangeType ExchangeType { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public long Candles { get; set; }
        public DateTime DownloadedAt { get; set; }
    }

    public async Task<List<CacheEntry>> ListAsync(CancellationToken ct)
    {
        var ranges = await _db.MarketCandleRanges.AsNoTracking()
            .OrderBy(r => r.ExchangeType).ThenBy(r => r.Symbol).ThenBy(r => r.Timeframe).ThenBy(r => r.FromUtc)
            .ToListAsync(ct);
        var counts = await _db.MarketCandles.AsNoTracking()
            .GroupBy(c => new { c.ExchangeType, c.Symbol, c.Timeframe })
            .Select(g => new { g.Key.ExchangeType, g.Key.Symbol, g.Key.Timeframe, Count = g.LongCount() })
            .ToListAsync(ct);
        var countMap = counts.ToDictionary(x => (x.ExchangeType, x.Symbol, x.Timeframe), x => x.Count);

        return ranges.Select(r => new CacheEntry
        {
            ExchangeType = r.ExchangeType,
            Symbol = r.Symbol,
            Timeframe = r.Timeframe,
            FromUtc = r.FromUtc,
            ToUtc = r.ToUtc,
            DownloadedAt = r.DownloadedAt,
            Candles = countMap.TryGetValue((r.ExchangeType, r.Symbol, r.Timeframe), out var n) ? n : 0
        }).ToList();
    }

    /// <summary>Drops cached candles and coverage for one key (any null filter = "all").</summary>
    public async Task<int> ClearAsync(ExchangeType? exchange, string? symbol, string? timeframe, CancellationToken ct)
    {
        symbol = symbol?.Trim().ToUpperInvariant();
        timeframe = timeframe?.Trim().ToLowerInvariant();

        var candles = _db.MarketCandles.AsQueryable();
        var ranges = _db.MarketCandleRanges.AsQueryable();
        if (exchange.HasValue) { candles = candles.Where(c => c.ExchangeType == exchange); ranges = ranges.Where(r => r.ExchangeType == exchange); }
        if (!string.IsNullOrEmpty(symbol)) { candles = candles.Where(c => c.Symbol == symbol); ranges = ranges.Where(r => r.Symbol == symbol); }
        if (!string.IsNullOrEmpty(timeframe)) { candles = candles.Where(c => c.Timeframe == timeframe); ranges = ranges.Where(r => r.Timeframe == timeframe); }

        var removed = await candles.ExecuteDeleteAsync(ct);
        await ranges.ExecuteDeleteAsync(ct);
        return removed;
    }

    private static DateTime FloorMinute(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
