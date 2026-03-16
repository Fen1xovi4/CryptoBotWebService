namespace CryptoBotWeb.Core.DTOs;

public class TwoFactorSetupResponse
{
    public string SecretKey { get; set; } = string.Empty;
    public string QrCodeUri { get; set; } = string.Empty;
}

public class TwoFactorVerifyRequest
{
    public string Code { get; set; } = string.Empty;
}

public class TwoFactorStatusResponse
{
    public bool IsEnabled { get; set; }
}

public class TwoFactorDisableRequest
{
    public string Code { get; set; } = string.Empty;
}
