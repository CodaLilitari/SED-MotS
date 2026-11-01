using System.Diagnostics;
using System.Text;

namespace Sed.Formats.NativeCore;

internal sealed class CppCoreProcessFactory : ICppCoreProcessFactory
{
    public ICppCoreProcess Start(ProcessStartInfo start) => new Child(Process.Start(start)
        ?? throw new IOException("The native bridge process did not start."));

    private sealed class Child(Process process) : ICppCoreProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public bool HasExited => process.HasExited;
        public int? Id => process.Id;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void KillTree() { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        public void Dispose() => process.Dispose();
    }
}

internal sealed record CppCoreProcessResult(int ExitCode, byte[] Output, string StandardError);

internal static class CppCoreProcessRunner
{
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(5);

    internal static async Task<CppCoreProcessResult> ExecuteAsync(string executable, string[] arguments, byte[] input,
        ICppCoreProcessFactory factory, Action<int?> started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var child = factory.Start(start);
        started(child.Id);
        using var ioStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<byte[]> stdout = ReadBoundedAsync(child.StandardOutput, CppCoreInspector.MaxOutputBytes, "stdout_limit", ioStop.Token);
        Task<byte[]> stderr = ReadBoundedAsync(child.StandardError, CppCoreInspector.MaxDiagnosticBytes, "stderr_limit", ioStop.Token);
        Task write = WriteAsync(child.StandardInput, input, ioStop.Token);
        Task exit = child.WaitForExitAsync(ioStop.Token);
        Task[] all = [stdout, stderr, write, exit];
        var pending = all.ToList();
        try
        {
            // Observe each fault promptly; waiting for WhenAll first could
            // deadlock behind a child that stopped consuming stdin.
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            return new(child.ExitCode, stdout.Result, Encoding.UTF8.GetString(stderr.Result));
        }
        catch (Exception error)
        {
            ioStop.Cancel();
            bool confirmed = false;
            try { child.KillTree(); }
            catch (Exception stopError) when (stopError is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try
            {
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(CleanupDeadline).ConfigureAwait(false);
                confirmed = child.HasExited;
            }
            catch (Exception waitError) when (waitError is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            // Closing our handles also releases reads whose descendants kept
            // a pipe inherited. This is not an OS sandbox or a claim that an
            // escaping descendant/session has been contained.
            foreach (Stream stream in new[] { child.StandardInput, child.StandardOutput, child.StandardError })
                try { stream.Dispose(); } catch (IOException) { }
            try { await Task.WhenAll(all).WaitAsync(CleanupDeadline).ConfigureAwait(false); }
            catch { /* Observe all task faults; preserve the initiating error. */ }
            if (!confirmed)
                throw new CppCoreFault("termination_unconfirmed", "Native worker exit could not be confirmed after stopping the owned process tree.", false, error);
            if (error is CppCoreFault fault) throw new CppCoreFault(fault.Code, fault.Message, true, fault);
            if (error is OperationCanceledException) throw;
            throw new CppCoreFault("process_io_error", "Native worker pipe or process operation failed: " + error.Message, true, error);
        }
    }

    private static async Task WriteAsync(Stream stream, byte[] input, CancellationToken token)
    {
        try
        {
            await stream.WriteAsync(input, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { stream.Dispose(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, string code, CancellationToken token)
    {
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)result.Length)), token).ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            result.Write(buffer, 0, count);
            if (result.Length > maximum) throw new CppCoreFault(code, "Native worker exceeded its bounded " + code.Replace('_', ' ') + ".");
        }
    }
}
