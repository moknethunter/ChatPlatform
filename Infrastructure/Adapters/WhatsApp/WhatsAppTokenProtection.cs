using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Use one encryption purpose for new tokens and read tokens saved by the older account controller.</summary>
public sealed class WhatsAppTokenProtection
{
    private readonly IDataProtector _current;
    private readonly IDataProtector _legacy;

    public WhatsAppTokenProtection(IDataProtectionProvider provider)
    {
        _current = provider.CreateProtector("BusinessToken");
        _legacy = provider.CreateProtector("WhatsApp.AccessTokens");
    }

    public string Protect(string token) => _current.Protect(token);

    public string Unprotect(string encrypted)
    {
        try { return _current.Unprotect(encrypted); }
        catch (CryptographicException) { return _legacy.Unprotect(encrypted); }
    }
}
