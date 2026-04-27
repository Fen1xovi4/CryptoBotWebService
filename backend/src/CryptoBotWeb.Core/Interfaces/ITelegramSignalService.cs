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
}
