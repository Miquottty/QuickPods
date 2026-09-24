using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using QuickPods.Spike.BluetoothKs.Runtime;

namespace QuickPods.Spike.BluetoothKs.Tests;

public sealed class KillOnCloseJobTests
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void NamedCreateRefusesToJoinAnExistingJob()
    {
        string jobName = $@"Global\QuickPods.BluetoothKs.Tests.{Guid.NewGuid():N}";
        using var first = KillOnCloseJob.Create(jobName);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => KillOnCloseJob.Create(jobName));

        Assert.Contains("refusing to join", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedJobRemainsPoisonedUntilItsDescendantTreeIsEmpty()
    {
        string jobName = $@"Global\QuickPods.BluetoothKs.Tests.{Guid.NewGuid():N}";
        var job = KillOnCloseJob.Create(jobName);
        var parentErrors = new ConcurrentQueue<string>();
        using Process parent = StartPausedParent();
        parent.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                parentErrors.Enqueue(e.Data);
            }
        };
        parent.BeginErrorReadLine();
        int? descendantProcessId = null;
        try
        {
            job.Assign(parent);
            string? readyLine = await RunStageAsync(
                "wait for the paused parent to report ready",
                parent,
                parentErrors,
                token => parent.StandardOutput.ReadLineAsync(token).AsTask());
            Assert.Equal("ready", readyLine);

            await RunStageAsync(
                "send the spawn command to the parent",
                parent,
                parentErrors,
                async token =>
                {
                    await parent.StandardInput.WriteLineAsync("spawn".AsMemory(), token);
                    parent.StandardInput.Close();
                    return true;
                });
            string? processIdLine = await RunStageAsync(
                "read the descendant process ID from the parent",
                parent,
                parentErrors,
                token => parent.StandardOutput.ReadLineAsync(token).AsTask());
            Assert.True(int.TryParse(
                processIdLine,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsedProcessId));
            descendantProcessId = parsedProcessId;
            await RunStageAsync(
                "wait for the parent to exit",
                parent,
                parentErrors,
                async token =>
                {
                    await parent.WaitForExitAsync(token);
                    return true;
                });
            Assert.True(IsProcessRunning(parsedProcessId));

            JobRecoveryStatus recovered = await KillOnCloseJob.RecoverNamedAsync(
                jobName,
                RecoveryTimeout);
            Assert.Equal(JobRecoveryStatus.Recovered, recovered);
            Assert.False(IsProcessRunning(parsedProcessId));

            job.Dispose();
            JobRecoveryStatus cleared = await KillOnCloseJob.RecoverNamedAsync(
                jobName,
                RecoveryTimeout);
            Assert.Equal(JobRecoveryStatus.NoPriorJob, cleared);
        }
        finally
        {
            job.Dispose();
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
                await parent.WaitForExitAsync(CancellationToken.None);
            }

            if (descendantProcessId is int processId && IsProcessRunning(processId))
            {
                using var descendant = Process.GetProcessById(processId);
                descendant.Kill(entireProcessTree: true);
                await descendant.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    // Each stage gets its own budget, started when the stage begins, so a slow
    // powershell.exe cold start on a CI runner cannot eat into later stages.
    private static async Task<T> RunStageAsync<T>(
        string stage,
        Process parent,
        ConcurrentQueue<string> parentErrors,
        Func<CancellationToken, Task<T>> operation)
    {
        using var timeout = new CancellationTokenSource(StageTimeout);
        try
        {
            return await operation(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            string parentState = parent.HasExited
                ? $"exited with code {parent.ExitCode}"
                : "still running";
            string stderr = parentErrors.IsEmpty
                ? "<none>"
                : string.Join(Environment.NewLine, parentErrors);
            throw new TimeoutException(
                $"Stage '{stage}' did not complete within {StageTimeout.TotalSeconds:0.###} s. " +
                $"Parent process {parentState}. Parent stderr: {stderr}",
                exception);
        }
    }

    private static Process StartPausedParent()
    {
        const string command =
            "[Console]::Out.WriteLine('ready'); " +
            "$null = [Console]::In.ReadLine(); " +
            "$child = Start-Process -FilePath 'ping.exe' -ArgumentList '-t','127.0.0.1' -PassThru; " +
            "[Console]::Out.WriteLine($child.Id)";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("The paused job-test parent could not be started.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
