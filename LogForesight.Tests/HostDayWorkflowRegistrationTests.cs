using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using LogForesight.Web.Services;
using LogForesight.Core.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

public sealed class HostDayWorkflowRegistrationTests
{
    [Fact]
    public void StartupRegistersWorkflowStoreRunMonitorDependencyAndRecoveryHostedConsumer()
    {
        var services = new ServiceCollection();
        services.AddStorage(new WebAppSettings());
        var hostedBeforeServices = services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService));
        services.AddLogForesightServices();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(HostDayWorkflowStore));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(HostDayWorkflowService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(HostDayWorkflowRecoveryHostedService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(RunMonitorService));
        Assert.True(services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)) > hostedBeforeServices);
        Assert.Contains(typeof(HostDayWorkflowService), typeof(RunMonitorService).GetConstructors()
            .Single().GetParameters().Select(parameter => parameter.ParameterType));
    }
}
