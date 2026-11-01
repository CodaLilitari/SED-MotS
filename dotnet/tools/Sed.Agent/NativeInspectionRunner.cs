using System.Text.Json;
using System.Text.Json.Serialization;
using Sed.Formats.Automation;
using Sed.Formats.NativeCore;

namespace Sed.Agent;

/// <summary>Explicit local CLI configuration for the optional native inspection component.</summary>
internal static class NativeInspectionRunner
{
    private sealed record WorkerConfiguration(int SchemaVersion, string ExecutableSha256,
        string SourceManifestSha256, int TimeoutSeconds = 20);

    internal static async Task<CppCoreInspectionObservation> RunAsync(string inputPath,
        string kind, string format, string configurationPath)
    {
        var projectKind = kind switch
        {
            "jk" => CppCoreProjectKind.Jk,
            "mots" => CppCoreProjectKind.Mots,
            "ijim" => CppCoreProjectKind.Ijim,
            _ => throw new AgentRequestException("Native inspection project kind must be jk, mots or ijim.")
        };
        var operation = format switch
        {
            "jkl" => CppCoreOperation.InspectJkl,
            "native-sed" => CppCoreOperation.InspectNativeSed,
            _ => throw new AgentRequestException("Native inspection format must be jkl or native-sed. Legacy text .sed is imported by the existing inspect command.")
        };
        string fullConfigurationPath = Path.GetFullPath(configurationPath);
        byte[] configurationBytes = await ReadBoundedAsync(fullConfigurationPath, 16 * 1024);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false,
            MaxDepth = 4
        };
        var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(configurationBytes, jsonOptions)
            ?? throw new AgentRequestException("Expected a native worker configuration object.");
        if (configuration.SchemaVersion != 1 || configuration.TimeoutSeconds is < 1 or > 60)
            throw new AgentRequestException("Native worker configuration needs schemaVersion 1 and a timeoutSeconds between 1 and 60.");
        // The config cannot supply arbitrary process arguments, executable paths or shell commands.
        // Its fixed sibling executable is an explicitly installed, hash-bound native component.
        string executable = Path.Combine(Path.GetDirectoryName(fullConfigurationPath)!,
            OperatingSystem.IsWindows() ? "sed-cpp-bridge.exe" : "sed-cpp-bridge");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The configured native inspection component is not installed beside worker-config.json.", executable);
        var options = new CppCoreInspectionOptions(executable, configuration.ExecutableSha256,
            configuration.SourceManifestSha256, TimeSpan.FromSeconds(configuration.TimeoutSeconds));
        byte[] input = await ReadBoundedAsync(Path.GetFullPath(inputPath), 16 * 1024 * 1024);
        return await CppCoreInspector.InspectAsync(options, operation, projectKind, input);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes)
    {
        var metadata = new FileInfo(path);
        if (!metadata.Exists) throw new FileNotFoundException("Native inspection input is not a file.", path);
        if (metadata.Length == 0 || metadata.Length > maximumBytes)
            throw new AgentRequestException($"Native inspection requires a nonempty file no larger than {maximumBytes} bytes.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > maximumBytes)
            throw new AgentRequestException($"Native inspection input exceeds the {maximumBytes}-byte limit.");
        using var bytes = new MemoryStream((int)file.Length);
        byte[] buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await file.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + read > maximumBytes)
                    throw new AgentRequestException($"Native inspection input exceeds the {maximumBytes}-byte limit.");
                bytes.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        { throw new IOException("Native inspection file read exceeded its 20-second deadline.", error); }
        return bytes.ToArray();
    }
}
