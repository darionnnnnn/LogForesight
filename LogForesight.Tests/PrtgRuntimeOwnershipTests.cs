using System.Net;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgRuntimeOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-runtime-owner-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StatusReportsActualLoadedSqliteRuntimeWithoutConnectionString()
    {
        var database = Path.Combine(_directory, "Db", "runtime-owner.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={database};Pooling=False"
        }, _directory);
        var controller = Create(backend, new string('A', 64), IPAddress.Loopback);

        var result = Assert.IsType<OkObjectResult>(controller.Status());
        var envelope = Assert.IsType<ApiResponse<PrtgRuntimeOwnershipDto>>(result.Value);
        var metadata = Assert.IsType<PrtgRuntimeOwnershipDto>(envelope.Data);
        Assert.Equal(Environment.ProcessId, metadata.ProcessId);
        Assert.True(metadata.ProcessStartUtcTicks > 0);
        Assert.Equal(Path.GetFullPath(_directory), metadata.DataRoot);
        Assert.Equal("Sqlite", metadata.Provider);
        Assert.Equal(Path.GetFullPath(database), metadata.DatabasePath);
        Assert.Equal(Path.GetFullPath(typeof(PrtgRuntimeOwnershipController).Assembly.Location), metadata.WebAssemblyLocation);
        Assert.Equal(typeof(PrtgRuntimeOwnershipController).Assembly.ManifestModule.ModuleVersionId.ToString("D"), metadata.WebAssemblyMvid);
        Assert.Equal(Path.GetFullPath(typeof(StorageBackend).Assembly.Location), metadata.CoreAssemblyLocation);
        Assert.Equal(typeof(StorageBackend).Assembly.ManifestModule.ModuleVersionId.ToString("D"), metadata.CoreAssemblyMvid);
        Assert.Equal(new string('A', 64), metadata.DescriptorSha256);
        var json = JsonSerializer.Serialize(metadata);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusRejectsForwardedAndNonLoopbackCallers()
    {
        var backend = CreateBackend();
        var forwarded = Create(backend, new string('B', 64), IPAddress.Loopback);
        forwarded.Request.Headers["X-Original-For"] = "203.0.113.8";
        Assert.IsType<ForbidResult>(forwarded.Status());

        var remote = Create(backend, new string('B', 64), IPAddress.Parse("192.0.2.15"));
        Assert.IsType<ForbidResult>(remote.Status());
    }

    [Fact]
    public void StatusFailsClosedWhenDescriptorHashIsMissingOrMalformed()
    {
        var backend = CreateBackend();
        var missing = Create(backend, null, IPAddress.Loopback);
        var missingResult = Assert.IsType<ObjectResult>(missing.Status());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, missingResult.StatusCode);

        var malformed = Create(backend, "not-a-sha256", IPAddress.Loopback);
        var malformedResult = Assert.IsType<ObjectResult>(malformed.Status());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, malformedResult.StatusCode);
    }

    [Fact]
    public void StatusRequiresMaintainCapability()
    {
        var controller = Create(CreateBackend(), new string('C', 64), IPAddress.Loopback,
            FakeCurrentUser.WithCapabilities(Capability.ViewAll));
        Assert.IsType<ForbidResult>(controller.Status());
        var permission = typeof(PrtgRuntimeOwnershipController)
            .GetCustomAttributes(typeof(LogForesight.Web.Filters.PermissionAttribute), inherit: true)
            .Cast<LogForesight.Web.Filters.PermissionAttribute>().Single();
        Assert.Contains(Capability.Maintain, permission.Arguments!.Cast<Capability[]>().Single());
    }

    private StorageBackend CreateBackend() => new(new StorageSettings { Type = "Sqlite" }, _directory);

    private static PrtgRuntimeOwnershipController Create(StorageBackend backend, string? descriptorHash,
        IPAddress remoteAddress, ICurrentUser? user = null)
    {
        var values = new Dictionary<string, string?>();
        if (descriptorHash is not null) values["E2:ProviderDescriptorSha256"] = descriptorHash;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var controller = new PrtgRuntimeOwnershipController(backend, configuration,
            user ?? FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
