using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace DaveIdentity;

public sealed class DaveIdentityOptions
{
    public required Uri Issuer { get; init; }

    public required string StatePath { get; init; }

    public string? TokenSigningCertificateThumbprint { get; init; }

    public string? EarClientCertificateAuthorityThumbprint { get; init; }

    public bool AllowEphemeralCertificates { get; init; }
}

public static class DaveIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddDaveIdentity(
        this IServiceCollection services,
        DaveIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<IIdentityStatePersistence>(
            _ => new JsonSnapshotIdentityPersistence(options.StatePath));
        services.AddSingleton<IdentityStateStore>();
        services.AddSingleton<IIdentityUserStore>(provider => provider.GetRequiredService<IdentityStateStore>());
        services.AddSingleton<IOAuthClientStore>(provider => provider.GetRequiredService<IdentityStateStore>());
        services.AddSingleton<IOAuthGrantStore>(provider => provider.GetRequiredService<IdentityStateStore>());
        services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<IdentityUserRecord>,
            Microsoft.AspNetCore.Identity.PasswordHasher<IdentityUserRecord>>();
        services.AddSingleton<LocalUserService>();
        services.AddSingleton(_ => CreateEarCertificateAuthority(options));
        services.AddSingleton<EarEnrollmentService>();
        services.AddSingleton<MtlsCertificateBindingValidator>();
        services.AddDbContext<DaveIdentityProtocolDbContext>(database =>
        {
            database.UseInMemoryDatabase("DaveIdentityProtocol");
            database.UseOpenIddict();
        });
        services.AddScoped<OpenIddictProtocolInitializer>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/identity/login";
                options.AccessDeniedPath = "/identity/access-denied";
                options.Cookie.Name = "__Host-DaveBrain";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.SlidingExpiration = true;
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(DaveIdentityPolicies.DashboardRead, policy =>
                policy.RequireAuthenticatedUser())
            .AddPolicy(DaveIdentityPolicies.ConfigurationManagement, RequireAdmin)
            .AddPolicy(DaveIdentityPolicies.HubManagement, RequireAdmin)
            .AddPolicy(DaveIdentityPolicies.EarManagement, RequireAdmin)
            .AddPolicy(DaveIdentityPolicies.DiagnosticCommands, RequireAdmin);

        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore()
                .UseDbContext<DaveIdentityProtocolDbContext>())
            .AddServer(openIddict =>
            {
                openIddict.SetIssuer(options.Issuer);
                openIddict.SetAuthorizationEndpointUris("/connect/authorize");
                openIddict.SetTokenEndpointUris("/connect/token");
                openIddict.SetDeviceAuthorizationEndpointUris("/connect/device");
                openIddict.SetEndUserVerificationEndpointUris("/connect/verify");
                openIddict.SetRevocationEndpointUris("/connect/revocation");
                openIddict.SetUserInfoEndpointUris("/connect/userinfo");
                openIddict.AllowAuthorizationCodeFlow();
                openIddict.AllowDeviceAuthorizationFlow();
                openIddict.AllowRefreshTokenFlow();
                openIddict.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                openIddict.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                openIddict.UseClientCertificateBoundRefreshTokens();
                openIddict.RequireProofKeyForCodeExchange();
                openIddict.RegisterScopes(
                    "davebrain.read",
                    "davebrain.admin",
                    "davebrain.ear.enroll",
                    "davebrain.ear.command");
                ConfigureTokenCertificates(openIddict, options);
                openIddict.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ProcessSignInContext>(
                    builder => builder.UseInlineHandler(context =>
                    {
                        // Only a certificate-presented refresh can mint a DaveEar command token.
                        if (!string.Equals(context.ClientId, "DaveEar", StringComparison.Ordinal) ||
                            !string.Equals(
                                context.Request.GrantType,
                                OpenIddictConstants.GrantTypes.RefreshToken,
                                StringComparison.Ordinal))
                        {
                            return default;
                        }

                        var certificate = context.Transaction.RemoteCertificate;
                        if (certificate is null)
                        {
                            context.Reject(
                                OpenIddictConstants.Errors.InvalidGrant,
                                "A client certificate is required when refreshing DaveEar tokens.");
                            return default;
                        }

                        return default;
                    }));
                openIddict.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
            });

        return services;
    }

    public static IEndpointRouteBuilder MapDaveIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/openid-configuration", (DaveIdentityOptions options) =>
            Results.Json(new
            {
                issuer = options.Issuer.AbsoluteUri.TrimEnd('/'),
                authorization_endpoint = new Uri(options.Issuer, "/connect/authorize").AbsoluteUri,
                token_endpoint = new Uri(options.Issuer, "/connect/token").AbsoluteUri,
                device_authorization_endpoint = new Uri(options.Issuer, "/connect/device").AbsoluteUri,
                revocation_endpoint = new Uri(options.Issuer, "/connect/revocation").AbsoluteUri,
                jwks_uri = new Uri(options.Issuer, "/.well-known/jwks").AbsoluteUri,
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code" },
                code_challenge_methods_supported = new[] { "S256" },
            })).AllowAnonymous();
        endpoints.MapGet("/.well-known/jwks", () => Results.Json(new { keys = Array.Empty<object>() }))
            .AllowAnonymous();
        return endpoints;
    }

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole(DaveRoles.Admin);

    private static EarCertificateAuthority CreateEarCertificateAuthority(DaveIdentityOptions options)
    {
        if (options.AllowEphemeralCertificates &&
            string.IsNullOrWhiteSpace(options.EarClientCertificateAuthorityThumbprint))
        {
            return EarCertificateAuthority.CreateEphemeralForDevelopment();
        }

        return new EarCertificateAuthority(CertificateStoreLoader.LoadLocalMachineCertificate(
            options.EarClientCertificateAuthorityThumbprint
                ?? throw new InvalidOperationException(
                    "An Ear client-certificate authority thumbprint is required outside development.")));
    }

    private static void ConfigureTokenCertificates(
        Microsoft.Extensions.DependencyInjection.OpenIddictServerBuilder openIddict,
        DaveIdentityOptions options)
    {
        if (options.AllowEphemeralCertificates &&
            string.IsNullOrWhiteSpace(options.TokenSigningCertificateThumbprint))
        {
            openIddict.AddDevelopmentEncryptionCertificate();
            openIddict.AddDevelopmentSigningCertificate();
            return;
        }

        var certificate = CertificateStoreLoader.LoadLocalMachineCertificate(
            options.TokenSigningCertificateThumbprint
                ?? throw new InvalidOperationException(
                    "A token-signing certificate thumbprint is required outside development."));
        openIddict.AddEncryptionCertificate(certificate);
        openIddict.AddSigningCertificate(certificate);
    }
}

public static class DaveIdentityPolicies
{
    public const string DashboardRead = "DashboardRead";
    public const string ConfigurationManagement = "ConfigurationManagement";
    public const string HubManagement = "HubManagement";
    public const string EarManagement = "EarManagement";
    public const string DiagnosticCommands = "DiagnosticCommands";
}
