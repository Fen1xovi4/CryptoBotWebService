using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// MaratG+Grid: the MaratG handler bound to the MaratGGrid type. All grid behaviour lives in
/// EmaBounceHandler.Grid.cs and switches on strategy.Type, so this class only claims the type.
/// </summary>
public class MaratGGridHandler : EmaBounceHandler
{
    public override string StrategyType => StrategyTypes.MaratGGrid;

    public MaratGGridHandler(AppDbContext db, ILogger<EmaBounceHandler> logger,
        ITelegramSignalService telegramSignalService, IQuoteStreamService quotes,
        IOrderStreamService orderStream)
        : base(db, logger, telegramSignalService, quotes, orderStream)
    {
    }
}
