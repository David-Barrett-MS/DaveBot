using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using DaveIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace DaveIdentity.Tests;

public sealed class IdentityFoundationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DaveBot-{Guid.NewGuid():N}");

    [Fact]
    public async Task DefaultAdministratorRequiresOneTimePasswordChange()
    {
        var users = await CreateUserServiceAsync();
        var initial = await users.ValidatePasswordAsync("Dave", "Dave");

        Assert.Equal(SignInResult.Succeeded, initial.Result);
        Assert.NotNull(initial.User);
        Assert.Contains(DaveRoles.Admin, initial.User.Roles);
        Assert.True(initial.User.MustChangePassword);
        Assert.DoesNotContain("Dave", initial.User.PasswordHash);

        await users.CompleteInitialPasswordChangeAsync(
            initial.User.Id,
            "Dave",
            "NewPass8");
        var oldPassword = await users.ValidatePasswordAsync("Dave", "Dave");
        var changedPassword = await users.ValidatePasswordAsync(
            "Dave",
            "NewPass8");

        Assert.Equal(SignInResult.InvalidCredentials, oldPassword.Result);
        Assert.Equal(SignInResult.Succeeded, changedPassword.Result);
        Assert.False(changedPassword.User?.MustChangePassword);
    }

    [Fact]
    public async Task InvalidPasswordLocksUserAfterFiveAttempts()
    {
        var users = await CreateUserServiceAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await users.ValidatePasswordAsync("dave", "incorrect-password");
        }

        var result = await users.ValidatePasswordAsync("dave", "Dave");

        Assert.Equal(SignInResult.LockedOut, result.Result);
    }

    [Fact]
    public async Task AccountManagementChangesRolesAndDisablesAccess()
    {
        var users = await CreateUserServiceAsync();
        var user = await users.CreateUserAsync(
            "Alice",
            "NewPass8",
            [DaveRoles.User]);

        await users.SetRolesAsync(user.Id, [DaveRoles.Admin]);
        await users.SetEnabledAsync(user.Id, enabled: false);

        Assert.Contains(
            DaveRoles.Admin,
            users.GetUsers().Single(candidate => candidate.Id == user.Id).Roles);
        var result = await users.ValidatePasswordAsync("Alice", "NewPass8");
        Assert.Equal(SignInResult.Disabled, result.Result);
    }

    [Fact]
    public void CertificateAuthorityValidatesOnlyBoundEarCertificate()
    {
        var authority = EarCertificateAuthority.CreateEphemeralForDevelopment();
        using var earKey = RSA.Create(2048);
        using var earCertificate = authority.IssueClientCertificate(
            earKey,
            "Kitchen Ear",
            DateTimeOffset.UtcNow.AddDays(30));

        var thumbprint = EarCertificateAuthority.CalculateThumbprint(earCertificate);

        Assert.True(authority.ValidateClientCertificate(earCertificate, thumbprint));
        Assert.False(authority.ValidateClientCertificate(earCertificate, "not-the-thumbprint"));
    }

    [Fact]
    public async Task EnrollmentBindsCertificateWithoutPersistingPrivateKey()
    {
        var persistence = new JsonSnapshotIdentityPersistence(Path.Combine(_directory, "identity.json"));
        var stateStore = new IdentityStateStore(persistence);
        await stateStore.InitializeAsync();
        var authority = EarCertificateAuthority.CreateEphemeralForDevelopment();
        var enrollment = new EarEnrollmentService(stateStore, authority);
        using var earKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Kitchen Ear",
            earKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var result = await enrollment.EnrollAsync(
            "Kitchen",
            Guid.NewGuid(),
            "https://brain.daves.tips",
            "administrator-subject",
            request.CreateSigningRequestPem());
        using var certificate = X509Certificate2.CreateFromPem(result.CertificatePem);
        using var certificateKey = certificate.GetRSAPublicKey();

        Assert.NotNull(certificateKey);
        Assert.Equal(earKey.ExportSubjectPublicKeyInfo(), certificateKey.ExportSubjectPublicKeyInfo());
        var usages = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Single()
            .EnhancedKeyUsages;
        Assert.Contains(usages.Cast<Oid>(), usage => usage.Value == "1.3.6.1.5.5.7.3.2");
        Assert.Equal("https://brain.daves.tips", result.Ear.OAuthIssuer);
        Assert.True(await enrollment.IsBoundCertificateAsync(result.Ear.Id, certificate));
        Assert.True(await enrollment.IsBoundCertificateForOAuthSubjectAsync(
            "DaveEar",
            "administrator-subject",
            certificate));
        Assert.False(await enrollment.IsBoundCertificateForOAuthSubjectAsync(
            "DaveEar",
            "another-subject",
            certificate));
        Assert.DoesNotContain("PRIVATE KEY", result.CertificatePem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MtlsBindingRequiresMatchingTokenCertificateAndEar()
    {
        var persistence = new JsonSnapshotIdentityPersistence(Path.Combine(_directory, "identity.json"));
        var stateStore = new IdentityStateStore(persistence);
        await stateStore.InitializeAsync();
        var authority = EarCertificateAuthority.CreateEphemeralForDevelopment();
        var enrollment = new EarEnrollmentService(stateStore, authority);
        using var earKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Office Ear",
            earKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var enrolled = await enrollment.EnrollAsync(
            "Office",
            Guid.NewGuid(),
            "https://brain.daves.tips",
            "administrator-subject",
            request.CreateSigningRequestPem());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.CertificatePem);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(
                "cnf",
                $$"""{"x5t#S256":"{{enrolled.Ear.MtlsCertificateThumbprint}}"}""")]));
        var validator = new MtlsCertificateBindingValidator(enrollment);

        Assert.True(await validator.IsValidAsync(enrolled.Ear.Id, principal, certificate));

        await enrollment.RevokeAsync(enrolled.Ear.Id);
        Assert.False(await validator.IsValidAsync(enrolled.Ear.Id, principal, certificate));
    }

    [Fact]
    public async Task ProtocolStorePersistsOpenIddictApplicationsAcrossServiceRestarts()
    {
        var statePath = Path.Combine(_directory, "identity.json");

        using (var provider = CreateIdentityProvider(statePath))
        using (var scope = provider.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<DaveIdentityProtocolDbContext>();
            await database.Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<OpenIddictProtocolInitializer>().InitializeAsync();
        }

        using (var provider = CreateIdentityProvider(statePath))
        using (var scope = provider.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<DaveIdentityProtocolDbContext>();
            await database.Database.EnsureCreatedAsync();
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

            Assert.NotNull(await applications.FindByClientIdAsync("DaveEar"));
        }
    }

    [Fact]
    public async Task DeletingEarRemovesItsCertificateBinding()
    {
        var persistence = new JsonSnapshotIdentityPersistence(Path.Combine(_directory, "identity.json"));
        var stateStore = new IdentityStateStore(persistence);
        await stateStore.InitializeAsync();
        var authority = EarCertificateAuthority.CreateEphemeralForDevelopment();
        var enrollment = new EarEnrollmentService(stateStore, authority);
        using var earKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Obsolete Ear",
            earKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var enrolled = await enrollment.EnrollAsync(
            "Obsolete Ear",
            Guid.NewGuid(),
            "https://brain.daves.tips",
            "administrator-subject",
            request.CreateSigningRequestPem());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.CertificatePem);

        var deleted = await enrollment.DeleteAsync(enrolled.Ear.Id);

        Assert.Equal(enrolled.Ear.Id, deleted.Id);
        Assert.Empty(enrollment.GetEars());
        Assert.False(await enrollment.IsBoundCertificateAsync(enrolled.Ear.Id, certificate));
    }

    private static ServiceProvider CreateIdentityProvider(string statePath)
    {
        var services = new ServiceCollection();
        services.AddDaveIdentity(new DaveIdentityOptions
        {
            Issuer = new Uri("https://brain.example.test"),
            StatePath = statePath,
            AllowEphemeralCertificates = true,
        });
        return services.BuildServiceProvider();
    }

    private async Task<LocalUserService> CreateUserServiceAsync()
    {
        var persistence = new JsonSnapshotIdentityPersistence(Path.Combine(_directory, "identity.json"));
        var users = new LocalUserService(
            new IdentityStateStore(persistence),
            new PasswordHasher<IdentityUserRecord>());
        await users.InitializeAsync();
        return users;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
