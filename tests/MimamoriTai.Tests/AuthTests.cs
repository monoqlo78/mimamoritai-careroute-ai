using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Auth;
using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

public class AuthTests
{
    private static AuthOptions ValidOptions() => new()
    {
        Enabled = true,
        Authority = "https://contoso.ciamlogin.com/tenant-id/v2.0",
        ClientId = "client-id",
        ClientSecret = "client-secret"
    };

    [Fact]
    public void IsConfigured_False_WhenDisabled()
    {
        var options = ValidOptions();
        options.Enabled = false;
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_False_WhenAuthorityMissing()
    {
        var options = ValidOptions();
        options.Authority = "";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_False_WhenClientIdMissing()
    {
        var options = ValidOptions();
        options.ClientId = "";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_False_WhenClientSecretMissing()
    {
        var options = ValidOptions();
        options.ClientSecret = "";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_True_WhenAllPresent()
    {
        Assert.True(ValidOptions().IsConfigured);
    }

    [Fact]
    public void IsLineAuthority_True_ForLineAccessAuthority()
    {
        var options = ValidOptions();
        options.Authority = "https://access.line.me";
        Assert.True(options.IsLineAuthority);
    }

    [Fact]
    public void SupportsRemoteSignOut_False_ForLine()
    {
        // LINE's discovery document publishes no end_session_endpoint, so signing out
        // through the OIDC handler throws. Verified against
        // https://access.line.me/.well-known/openid-configuration.
        var options = ValidOptions();
        options.Authority = "https://access.line.me";
        Assert.False(options.SupportsRemoteSignOut);
    }

    [Fact]
    public void SupportsRemoteSignOut_True_ForEntraExternalId()
    {
        Assert.True(ValidOptions().SupportsRemoteSignOut);
    }

    [Fact]
    public void ResolveIdentityProvider_Line_WhenAuthorityIsLine_AndNoIdpClaim()
    {
        var options = ValidOptions();
        options.Authority = "https://access.line.me";
        Assert.Equal("line", options.ResolveIdentityProvider(null));
    }

    [Fact]
    public void ResolveIdentityProvider_Line_WhenIdpClaimContainsLine()
    {
        Assert.Equal("line", ValidOptions().ResolveIdentityProvider("LineFederation"));
    }

    [Fact]
    public void ResolveIdentityProvider_FallsBackToProviderName()
    {
        Assert.Equal("entra-external", ValidOptions().ResolveIdentityProvider(null));
    }

    [Fact]
    public void Current_ReportsLineProvider_ForDirectLineAuthority_WithoutIdpClaim()
    {
        // Direct LINE Login mints no "idp" claim, so the accessor must fall back to the
        // authority to stay consistent with how the AppUser row is provisioned.
        var appUserId = Guid.NewGuid();
        var identity = new ClaimsIdentity(authenticationType: "oidc");
        identity.AddClaim(new Claim("sub", "line-sub"));
        identity.AddClaim(new Claim(ClaimsCurrentUserAccessor.AppUserIdClaimType, appUserId.ToString()));

        var options = ValidOptions();
        options.Authority = "https://access.line.me";

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = Accessor(httpContext, options);

        Assert.Equal("line", accessor.Current!.IdentityProvider);
    }

    private static ClaimsCurrentUserAccessor Accessor(HttpContext? httpContext, AuthOptions options) =>
        new(new FakeHttpContextAccessor(httpContext), Microsoft.Extensions.Options.Options.Create(options));

    [Fact]
    public void Current_Null_ForUnauthenticatedPrincipal()
    {
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var accessor = Accessor(httpContext, ValidOptions());

        Assert.Null(accessor.Current);
    }

    [Fact]
    public void Current_Null_WhenNoHttpContext()
    {
        var accessor = Accessor(null, ValidOptions());
        Assert.Null(accessor.Current);
    }

    [Fact]
    public void Current_MapsOidSubNameAndUidClaims()
    {
        var appUserId = Guid.NewGuid();
        var identity = new ClaimsIdentity(authenticationType: "oidc");
        identity.AddClaim(new Claim("oid", "external-oid"));
        identity.AddClaim(new Claim("sub", "external-sub"));
        identity.AddClaim(new Claim("name", "山田太郎"));
        identity.AddClaim(new Claim(ClaimsCurrentUserAccessor.AppUserIdClaimType, appUserId.ToString()));

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = Accessor(httpContext, ValidOptions());

        var current = accessor.Current;
        Assert.NotNull(current);
        Assert.Equal(appUserId, current!.AppUserId);
        Assert.Equal("external-oid", current.ExternalSubject);
        Assert.Equal("山田太郎", current.DisplayName);
        Assert.Equal("entra-external", current.IdentityProvider);
        Assert.True(current.IsAuthenticated);
    }

    [Fact]
    public void Current_FallsBackToSub_WhenOidMissing()
    {
        var appUserId = Guid.NewGuid();
        var identity = new ClaimsIdentity(authenticationType: "oidc");
        identity.AddClaim(new Claim("sub", "line-sub"));
        identity.AddClaim(new Claim(ClaimsCurrentUserAccessor.AppUserIdClaimType, appUserId.ToString()));

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = Accessor(httpContext, ValidOptions());

        Assert.Equal("line-sub", accessor.Current!.ExternalSubject);
    }

    [Fact]
    public void Current_ReportsLineProvider_WhenIdpClaimContainsLine()
    {
        var appUserId = Guid.NewGuid();
        var identity = new ClaimsIdentity(authenticationType: "oidc");
        identity.AddClaim(new Claim("sub", "line-sub"));
        identity.AddClaim(new Claim("idp", "LineFederation"));
        identity.AddClaim(new Claim(ClaimsCurrentUserAccessor.AppUserIdClaimType, appUserId.ToString()));

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = Accessor(httpContext, ValidOptions());

        Assert.Equal("line", accessor.Current!.IdentityProvider);
    }

    [Fact]
    public void Current_Null_WhenAuthDisabled_EvenIfAuthenticated()
    {
        var appUserId = Guid.NewGuid();
        var identity = new ClaimsIdentity(authenticationType: "oidc");
        identity.AddClaim(new Claim("sub", "external-sub"));
        identity.AddClaim(new Claim(ClaimsCurrentUserAccessor.AppUserIdClaimType, appUserId.ToString()));

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var options = ValidOptions();
        options.Enabled = false;
        var accessor = Accessor(httpContext, options);

        Assert.Null(accessor.Current);
    }

    private sealed class FakeHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
