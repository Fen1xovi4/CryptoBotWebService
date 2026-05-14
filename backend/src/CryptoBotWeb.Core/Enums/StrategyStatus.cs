namespace CryptoBotWeb.Core.Enums;

public enum StrategyStatus
{
    Idle = 0,
    Running = 1,
    Stopped = 2,
    Error = 3,
    // Paused: handler tick is skipped (TradingHostedService filters Running only) but state
    // is preserved. Unlike Stop+Start, Pause+Resume keeps Batches / DcaOrders intact so a
    // live GridFloat grid can be inspected and reconfigured (e.g. RangePercent widened) without
    // tripping SyncFromExchangeOnStartup's "position alive but state empty" guard.
    Paused = 4
}
