namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Order-size and price constraints of one futures contract: quantity step, minimum quantity and
/// price tick. Served from a process-wide cache by the exchange services — these change rarely,
/// and fetching them on every order put an extra REST round-trip (on BingX: the whole contract
/// list) between "decided to trade" and "order on the book".
/// </summary>
public sealed record InstrumentRulesDto(decimal QtyStep, decimal MinQty, decimal PriceStep);
