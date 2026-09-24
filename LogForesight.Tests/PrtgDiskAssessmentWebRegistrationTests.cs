using System.Reflection;

using LogForesight.Core.Configuration;
using LogForesight.Core.Service;
using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskAssessmentWebRegistrationTests
{
    [Fact]
    public void WebServices_ResolveRuleAdminWithScopedDiskAssessment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-web-disk-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new WebAppSettings
        {
            Storage = new StorageSettings
            {
                Type = "Sqlite",
                DataRoot = directory,
                ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(settings);
        services.AddStorage(settings);
        services.AddLogForesightAuth(settings);
        services.AddLogForesightServices();

        using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        using (var scope = provider.CreateScope())
        {
            var assessor = scope.ServiceProvider.GetRequiredService<PrtgDiskAssessmentService>();
            var admin = scope.ServiceProvider.GetRequiredService<RuleAdminService>();

            Assert.NotNull(assessor);
            Assert.NotNull(admin);
            var injectedAssessor = typeof(RuleAdminService)
                .GetField("_diskAssessment", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(admin);
            Assert.Same(assessor, injectedAssessor);
            using var otherScope = provider.CreateScope();
            Assert.NotSame(assessor, otherScope.ServiceProvider.GetRequiredService<PrtgDiskAssessmentService>());
        }

        Directory.Delete(directory, recursive: true);
    }
}
