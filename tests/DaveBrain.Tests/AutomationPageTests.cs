using System.Reflection;
using DaveBrain.Pages.Automation;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Tests;

public sealed class AutomationPageTests
{
    [Fact]
    public void AutomationPageRequiresConfigurationManagement()
    {
        var authorization = typeof(IndexModel).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorization);
        Assert.Equal(DaveIdentityPolicies.ConfigurationManagement, authorization.Policy);
    }

    [Fact]
    public async Task ActionHandlerRejectsAnythingOtherThanAnExplicitStateChange()
    {
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        var model = new IndexModel(
            new AutomationConfigurationService(store),
            store,
            new InMemoryAutomationAuditStore());

        var result = await model.OnPostSaveActionAsync(
            null,
            "Invalid",
            Guid.NewGuid(),
            "shell.execute",
            null,
            null,
            null,
            true,
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Contains(
            model.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage.Contains("On/off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActionHandlerRejectsMissingServerSideFormValues()
    {
        var store = new HomeStateStore(new InMemoryPersistence());
        await store.InitializeAsync();
        var model = new IndexModel(
            new AutomationConfigurationService(store),
            store,
            new InMemoryAutomationAuditStore());

        var result = await model.OnPostSaveActionAsync(
            null,
            null,
            Guid.NewGuid(),
            null,
            null,
            null,
            null,
            true,
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Contains(
            model.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage.Contains("Action name is required", StringComparison.Ordinal));
    }

    private sealed class InMemoryPersistence : IHomeStatePersistence
    {
        public Task<HomeStateDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<HomeStateDocument?>(null);

        public Task SaveAsync(
            HomeStateDocument document,
            StoreRevision expectedRevision,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
