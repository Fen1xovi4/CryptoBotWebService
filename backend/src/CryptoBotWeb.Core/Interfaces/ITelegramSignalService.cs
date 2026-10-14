using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

public interface ITelegramSignalService
{
    Task SendOpenPositionSignalAsync(Strategy strategy, string symbol, string direction,
        decimal orderSize, decimal entryPrice, decimal takeProfit, decimal? stopLoss,
        CancellationToken ct = default);

    Task SendDcaSignalAsync(Strategy strategy, string symbol, string direction,
        int dcaLevel, decimal dcaQuoteAmount, decimal newAveragePrice, decimal newTakeProfit,
        CancellationToken ct = default);

    Task SendPositionClosedSignalAsync(Strategy strategy, string symbol, string direction,
        decimal pnlDollar, decimal pnlPercent,
        CancellationToken ct = default);

    /// <summary>Free-form operational alert (e.g. a bot stopped itself) to the strategy's Telegram bot.</summary>
    Task SendStrategyAlertAsync(Strategy strategy, string title, string details,
        CancellationToken ct = default);
}
