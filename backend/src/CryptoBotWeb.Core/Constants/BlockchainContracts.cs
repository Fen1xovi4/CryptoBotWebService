namespace CryptoBotWeb.Core.Constants;

public static class BlockchainContracts
{
    // TRC20 (Tron)
    public const string TRC20_USDT = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    public const string TRC20_USDC = "TEkxiTehnzSmSe2XqrBj4w32RUN966rdz8";

    // BEP20 (BSC)
    public const string BEP20_USDT = "0x55d398326f99059fF775485246999027B3197955";
    public const string BEP20_USDC = "0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d";

    public const decimal PaymentTolerance = 1.0m;
    public const int WalletLockMinutes = 30;
    public const int VerificationIntervalSeconds = 30;
}
