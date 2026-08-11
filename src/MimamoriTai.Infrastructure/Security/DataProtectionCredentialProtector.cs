using Microsoft.AspNetCore.DataProtection;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Security;

/// <summary>
/// The only production implementation of <see cref="ICredentialProtector"/>: wraps
/// ASP.NET Core Data Protection. The purpose string below is the key-derivation
/// "namespace" for this protector -- it must stay stable forever. Changing it (or
/// bumping past ".v1") would make every previously-saved SwitchBotConnection
/// undecryptable, so any future rotation needs an explicit migration (re-protect
/// under a new purpose while the old one is still resolvable), not just a rename
/// here.
/// </summary>
public sealed class DataProtectionCredentialProtector : ICredentialProtector
{
    /// <summary>Stable Data Protection purpose string. Do not change without a migration plan.</summary>
    public const string Purpose = "MimamoriTai.SwitchBotCredentials.v1";

    private readonly IDataProtector _protector;

    public DataProtectionCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
