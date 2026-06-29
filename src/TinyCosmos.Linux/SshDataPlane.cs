using TinyCosmos.Core;
using TinyCosmos.Protocol;

namespace TinyCosmos.Linux;

public sealed record SshTarget(
    string Host,
    int Port,
    string User,
    string IdentityFile,
    string KnownHostsFile,
    string? ControlPath);

public sealed record SshExecRequest(
    SshTarget Target,
    string WorkingDirectory,
    IReadOnlyList<string> Command,
    int TimeoutSeconds,
    int OutputLimitBytes,
    bool PseudoTerminal = false);

public sealed record SshCommandPlan(string FileName, IReadOnlyList<string> Arguments);

public sealed record SftpReadRequest(SshTarget Target, string RemotePath, string LocalPath, string BatchFilePath);

public sealed record SftpWriteRequest(SshTarget Target, string LocalPath, string RemotePath, string BatchFilePath, bool Executable);

public sealed record SftpListRequest(SshTarget Target, string RemotePath, string BatchFilePath);

public sealed record SftpBatchPlan(string FileName, IReadOnlyList<string> Arguments, string BatchText);

public static class SshCommandPlanner
{
    public static SshCommandPlan PlanExec(SshExecRequest request)
    {
        if (request.Command.Count == 0)
        {
            throw new ArgumentException("SSH command vector must not be empty.", nameof(request));
        }

        if (request.TimeoutSeconds is < 1 or > 86_400)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be between 1 second and 24 hours.");
        }

        if (request.OutputLimitBytes is < 1 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Output limit must be between 1 byte and 64 MiB.");
        }

        var remoteCommand = "cd " + ShellQuote(request.WorkingDirectory) + " && exec " + string.Join(' ', request.Command.Select(ShellQuote));
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=" + request.Target.KnownHostsFile,
            "-i", request.Target.IdentityFile,
            "-p", request.Target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (request.PseudoTerminal)
        {
            args.Add("-tt");
        }

        if (!string.IsNullOrWhiteSpace(request.Target.ControlPath))
        {
            args.Add("-o");
            args.Add("ControlMaster=auto");
            args.Add("-o");
            args.Add("ControlPersist=120");
            args.Add("-o");
            args.Add("ControlPath=" + request.Target.ControlPath);
        }

        args.Add(request.Target.User + "@" + request.Target.Host);
        args.Add("--");
        args.Add(remoteCommand);
        return new SshCommandPlan("/usr/bin/ssh", args);
    }

    public static string ShellQuote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

public sealed class SshGuestExecutor(SshTarget target)
{
    public async Task<ExecResult> ExecuteAsync(ExecPayload payload, CancellationToken cancellationToken = default)
    {
        var plan = SshCommandPlanner.PlanExec(new SshExecRequest(
            target,
            payload.WorkingDirectory,
            payload.Command,
            payload.TimeoutSeconds,
            payload.OutputLimitBytes,
            payload.PseudoTerminal));
        var run = await ProcessRunner.RunAsync(
            plan.FileName,
            plan.Arguments,
            timeout: TimeSpan.FromSeconds(payload.TimeoutSeconds),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var stdout = Truncate(run.Stdout, payload.OutputLimitBytes, out var stdoutTruncated);
        var stderr = Truncate(run.Stderr, payload.OutputLimitBytes, out var stderrTruncated);
        return new ExecResult(run.TimedOut ? 124 : run.ExitCode, stdout, stderr, stdoutTruncated || stderrTruncated);
    }

    private static string Truncate(string value, int limitBytes, out bool truncated)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= limitBytes)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, limitBytes));
    }
}

public static class SftpCommandPlanner
{
    public static SftpBatchPlan PlanRead(SftpReadRequest request)
    {
        ValidateRemotePath(request.RemotePath);
        ValidateLocalPath(request.LocalPath);
        ValidateLocalPath(request.BatchFilePath);
        return new SftpBatchPlan(
            "/usr/bin/sftp",
            BuildArguments(request.Target, request.BatchFilePath),
            "get -p " + BatchQuote(request.RemotePath) + " " + BatchQuote(request.LocalPath) + "\n");
    }

    public static SftpBatchPlan PlanWrite(SftpWriteRequest request)
    {
        ValidateLocalPath(request.LocalPath);
        ValidateRemotePath(request.RemotePath);
        ValidateLocalPath(request.BatchFilePath);
        var batch = "put -p " + BatchQuote(request.LocalPath) + " " + BatchQuote(request.RemotePath) + "\n";
        if (request.Executable)
        {
            batch += "chmod 755 " + BatchQuote(request.RemotePath) + "\n";
        }

        return new SftpBatchPlan("/usr/bin/sftp", BuildArguments(request.Target, request.BatchFilePath), batch);
    }

    public static SftpBatchPlan PlanList(SftpListRequest request)
    {
        ValidateRemotePath(request.RemotePath);
        ValidateLocalPath(request.BatchFilePath);
        return new SftpBatchPlan(
            "/usr/bin/sftp",
            BuildArguments(request.Target, request.BatchFilePath),
            "ls -la " + BatchQuote(request.RemotePath) + "\n");
    }

    private static IReadOnlyList<string> BuildArguments(SshTarget target, string batchFilePath)
    {
        var args = new List<string>
        {
            "-b", batchFilePath,
            "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=" + target.KnownHostsFile,
            "-i", target.IdentityFile,
            "-P", target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(target.ControlPath))
        {
            args.Add("-o");
            args.Add("ControlMaster=auto");
            args.Add("-o");
            args.Add("ControlPersist=120");
            args.Add("-o");
            args.Add("ControlPath=" + target.ControlPath);
        }

        args.Add(target.User + "@" + target.Host);
        return args;
    }

    private static string BatchQuote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\0') || path.Any(char.IsControl))
        {
            throw new ArgumentException("Guest SFTP paths must be absolute POSIX paths.", nameof(path));
        }

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                throw new ArgumentException("Guest SFTP paths must not contain '..' segments.", nameof(path));
            }
        }
    }

    private static void ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.Contains('\0') || path.Any(char.IsControl))
        {
            throw new ArgumentException("Local SFTP paths must be absolute paths.", nameof(path));
        }
    }
}
