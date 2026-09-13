using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace DaveIdentity;

public sealed class DaveIdentityProtocolDbContext(
    DbContextOptions<DaveIdentityProtocolDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
    }
}

public sealed class OpenIddictProtocolInitializer(
    IOpenIddictApplicationManager applicationManager)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureApplicationAsync(
            "DaveBrain.Web",
            "DaveBrain administration",
            [
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                "scp:openid",
                "scp:davebrain.read",
                "scp:davebrain.admin",
            ],
            cancellationToken);
        await EnsureApplicationAsync(
            "DaveEar",
            "DaveEar device client",
            [
                OpenIddictConstants.Permissions.Endpoints.DeviceAuthorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.DeviceCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                "scp:davebrain.ear.enroll",
                "scp:davebrain.ear.command",
            ],
            cancellationToken);
    }

    private async Task EnsureApplicationAsync(
        string clientId,
        string displayName,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        if (await applicationManager.FindByClientIdAsync(clientId, cancellationToken) is not null)
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            DisplayName = displayName,
        };
        foreach (var permission in permissions)
        {
            descriptor.Permissions.Add(permission);
        }

        await applicationManager.CreateAsync(descriptor, cancellationToken);
    }
}
