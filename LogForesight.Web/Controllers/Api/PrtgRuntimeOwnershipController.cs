using System.Diagnostics;
using System.Net;
using System.Reflection;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LogForesight.Web.Controllers.Api;

public sealed record PrtgRuntimeOwnershipDto(
    int ProcessId, long ProcessStartUtcTicks, string ProcessStartUtc,
    string DataRoot, string Provider, string DatabasePath,
    string WebAssemblyLocation, string WebAssemblyMvid,
    string CoreAssemblyLocation, string CoreAssemblyMvid,
    string DescriptorSha256);

/// <summary>Reports the actual loaded E2 runtime owner to a loopback Maintain caller.</summary>
[ApiController]
[Route("api/admin/prtg-runtime-ownership")]
[Permission(Capability.Maintain)]
public sealed class PrtgRuntimeOwnershipController : ControllerBase
{
    private readonly StorageBackend _backend;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUser _currentUser;

    public PrtgRuntimeOwnershipController(StorageBackend backend, IConfiguration configuration,
        ICurrentUser currentUser)
    {
        _backend = backend;
        _configuration = configuration;
        _currentUser = currentUser;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        if (!_currentUser.Has(Capability.Maintain)) return Forbid();
        if (!IsDirectLoopbackCaller()) return Forbid();

        var descriptorHash = _configuration["E2:ProviderDescriptorSha256"];
        if (descriptorHash is null || descriptorHash.Length != 64 ||
            descriptorHash.Any(character => !Uri.IsHexDigit(character)))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse.Fail("runtime_owner_unconfigured", "E2 runtime ownership is not configured."));

        try
        {
            using var context = _backend.CreateContext();
            if (!string.Equals(context.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite",
                    StringComparison.Ordinal) || context.Database.GetDbConnection() is not SqliteConnection sqlite)
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    ApiResponse.Fail("runtime_owner_provider_unsupported", "E2 runtime ownership requires the configured SQLite backend."));

            var sqliteOptions = new SqliteConnectionStringBuilder(sqlite.ConnectionString);
            if (string.IsNullOrWhiteSpace(sqliteOptions.DataSource) ||
                sqliteOptions.Mode == SqliteOpenMode.Memory || sqliteOptions.DataSource.StartsWith(':') ||
                sqliteOptions.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    ApiResponse.Fail("runtime_owner_database_unsupported", "E2 runtime ownership requires a file-backed SQLite database."));

            var webAssembly = typeof(PrtgRuntimeOwnershipController).Assembly;
            var coreAssembly = typeof(StorageBackend).Assembly;
            var process = Process.GetCurrentProcess();
            var processStartUtc = process.StartTime.ToUniversalTime();
            var metadata = new PrtgRuntimeOwnershipDto(
                Environment.ProcessId,
                processStartUtc.Ticks,
                processStartUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(_backend.DataRoot),
                "Sqlite",
                Path.GetFullPath(sqliteOptions.DataSource),
                LoadedLocation(webAssembly),
                webAssembly.ManifestModule.ModuleVersionId.ToString("D"),
                LoadedLocation(coreAssembly),
                coreAssembly.ManifestModule.ModuleVersionId.ToString("D"),
                descriptorHash.ToUpperInvariant());
            return Ok(ApiResponse<PrtgRuntimeOwnershipDto>.Ok(metadata));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                            IOException or NotSupportedException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse.Fail("runtime_owner_unavailable", "E2 runtime ownership metadata is unavailable."));
        }
    }

    private bool IsDirectLoopbackCaller()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;

        // ForwardedHeaders middleware preserves the pre-forwarding address in X-Original-For.
        // Refuse forwarded requests even when a trusted proxy rewrote RemoteIpAddress to loopback.
        return !Request.Headers.ContainsKey("X-Original-For") &&
               !Request.Headers.ContainsKey("X-Forwarded-For") &&
               !Request.Headers.ContainsKey("Forwarded");
    }

    private static string LoadedLocation(Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location))
            throw new InvalidOperationException("Runtime assembly has no physical loaded location.");
        return Path.GetFullPath(assembly.Location);
    }
}
