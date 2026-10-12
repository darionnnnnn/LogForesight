using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: BuildIdentityManifest <assembly-directory> <manifest-path>");
    return 2;
}

var directory = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var corePath = Path.Combine(directory, "LogForesight.Core.dll");
var webPath = Path.Combine(directory, "LogForesight.Web.dll");
if (!File.Exists(corePath) || !File.Exists(webPath))
{
    Console.Error.WriteLine("Both LogForesight.Core.dll and LogForesight.Web.dll must be present.");
    return 1;
}

// This utility is a separate process per output directory, so loading the two assemblies cannot
// bind to an identically named assembly from a previous build in the MSBuild process.
var context = new DirectoryAssemblyLoadContext(directory);
try
{
    var core = context.LoadFromAssemblyPath(corePath);
    var web = context.LoadFromAssemblyPath(webPath);
    var manifest = new BuildManifest(
        Schema: "logforesight-build-identity-v1",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Assemblies: new[] { Describe(web, webPath), Describe(core, corePath) });
    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    File.WriteAllText(manifestPath, json + Environment.NewLine);
    return 0;
}
finally
{
    context.Unload();
}

static AssemblyManifest Describe(Assembly assembly, string path)
{
    var stamp = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "LogForesight.BuildIdentity")?.Value;
    return new AssemblyManifest(
        Name: assembly.GetName().Name ?? "unknown",
        InformationalVersion: assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        ModuleVersionId: assembly.ManifestModule.ModuleVersionId,
        FileSha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        SourceStamp: stamp,
        SourceRevisionIdOverride: assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "LogForesight.BuildSourceRevisionIdOverride")?.Value);
}

sealed record BuildManifest(string Schema, DateTimeOffset CreatedAtUtc, IReadOnlyList<AssemblyManifest> Assemblies);
sealed record AssemblyManifest(string Name, string? InformationalVersion, Guid ModuleVersionId,
    string FileSha256, string? SourceStamp, string? SourceRevisionIdOverride);

sealed class DirectoryAssemblyLoadContext(string directory) : AssemblyLoadContext("build-identity-manifest", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name)) return null;
        var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
