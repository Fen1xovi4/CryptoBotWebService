using System.Text.Json;

namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// One entry of the public "top bots" leaderboard. Deliberately anonymous: no bot name, owner or
/// exchange account — only what someone needs to judge the result and replicate the settings.
/// </summary>
public class TopBotDto
{
    public Guid Id { get; set; }
    public string StrategyType { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string? Timeframe { get; set; }
    public string? Direction { get; set; }
    public bool IsRunning { get; set; }

    /// <summary>Realized PnL in the period ÷ peak capital invested in the period, in %.</summary>
    public decimal ReturnPercent { get; set; }
    /// <summary>Realized PnL (closed trades) inside the period, USDT.</summary>
    public decimal PnlUsd { get; set; }
    /// <summary>Peak cost of the open position(s) inside the period, USDT (notional, not margin).</summary>
    public decimal PeakCapitalUsd { get; set; }

    public int PeriodClosedTrades { get; set; }
    public decimal PeriodWinRate { get; set; }
    public int TotalClosedTrades { get; set; }
    public decimal TotalPnlUsd { get; set; }
    /// <summary>Days since the bot's first trade.</summary>
    public int ActiveDays { get; set; }

    /// <summary>The bot's settings, same shape as strategies.ConfigJson (runtime-only fields stripped).</summary>
    public JsonElement Config { get; set; }
}

public class TopBotsResponse
{
    public int PeriodDays { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<TopBotDto> Bots { get; set; } = new();
}

/// <summary>A trade reduced to what the leaderboard math needs.</summary>
public readonly record struct TopBotTrade(DateTime ExecutedAt, decimal Quantity, decimal Price, decimal? PnlDollar);
