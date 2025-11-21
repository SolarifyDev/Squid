using System.Diagnostics;
using Halibut;
using Newtonsoft.Json;

namespace Squid.Core.Commands.Tentacle;

public class StartScriptCommand
{
    [JsonConstructor]
    public StartScriptCommand(string scriptBody,
        ScriptIsolationLevel isolation,
        TimeSpan scriptIsolationMutexTimeout,
        string? isolationMutexName,
        string[] arguments,
        string? taskId)
    {
        Arguments = arguments;
        TaskId = taskId;
        ScriptBody = scriptBody;
        Isolation = isolation;
        ScriptIsolationMutexTimeout = scriptIsolationMutexTimeout;
        IsolationMutexName = isolationMutexName;
    }

    public StartScriptCommand(string scriptBody,
        ScriptIsolationLevel isolation,
        TimeSpan scriptIsolationMutexTimeout,
        string isolationMutexName,
        string[] arguments,
        string? taskId,
        params ScriptFile[] additionalFiles)
        : this(
            scriptBody,
            isolation,
            scriptIsolationMutexTimeout,
            isolationMutexName,
            arguments,
            taskId)
    {
        if (additionalFiles != null)
            Files.AddRange(additionalFiles);
    }

    public StartScriptCommand(string scriptBody,
        ScriptIsolationLevel isolation,
        TimeSpan scriptIsolationMutexTimeout,
        string isolationMutexName,
        string[] arguments,
        string? taskId,
        Dictionary<ScriptType, string> additionalScripts,
        params ScriptFile[] additionalFiles)
        : this(
            scriptBody,
            isolation,
            scriptIsolationMutexTimeout,
            isolationMutexName,
            arguments,
            taskId,
            additionalFiles)
    {
        if (additionalScripts == null || !additionalScripts.Any())
            return;

        foreach (var additionalScript in additionalScripts)
        {
            Scripts.Add(additionalScript.Key, additionalScript.Value);
        }
    }

    public string ScriptBody { get; }

    public ScriptIsolationLevel Isolation { get; }

    public Dictionary<ScriptType, string> Scripts { get; } = new Dictionary<ScriptType, string>();

    public List<ScriptFile> Files { get; } = new List<ScriptFile>();

    public string[] Arguments { get; }

    public string? TaskId { get; }

    public TimeSpan ScriptIsolationMutexTimeout { get; }
    public string? IsolationMutexName { get; }
}

public enum ScriptType
{
    PowerShell,
    Bash
}

public enum ScriptIsolationLevel
{
    NoIsolation,
    FullIsolation
}

public class ScriptTicket : IEquatable<ScriptTicket>
{
    public ScriptTicket(string taskId)
    {
        TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public string TaskId { get; }

    public bool Equals(ScriptTicket? other)
    {
        if (ReferenceEquals(null, other))
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return string.Equals(TaskId, other.TaskId, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
            return false;

        if (ReferenceEquals(this, obj))
            return true;

        if (obj.GetType() != GetType())
            return false;

        return Equals((ScriptTicket)obj);
    }

    public override int GetHashCode()
        => TaskId != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(TaskId) : 0;

    public static bool operator ==(ScriptTicket left, ScriptTicket right)
        => Equals(left, right);

    public static bool operator !=(ScriptTicket left, ScriptTicket right)
        => !Equals(left, right);

    public override string ToString()
        => TaskId;
}

public class ScriptFile
{
    [JsonConstructor]
    public ScriptFile(string name, DataStream contents, string? encryptionPassword)
    {
        Name = name;
        Contents = contents;
        EncryptionPassword = encryptionPassword;
    }

    public ScriptFile(string name, DataStream contents) : this(name, contents, null)
    {
    }

    public string Name { get; }

    public DataStream Contents { get; }

    public string? EncryptionPassword { get; }
}

public class ScriptStatusRequest
{
    public ScriptStatusRequest(ScriptTicket ticket, long lastLogSequence)
    {
        Ticket = ticket;
        LastLogSequence = lastLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public long LastLogSequence { get; }
}

public class ScriptStatusResponse
{
    public ScriptStatusResponse(
        ScriptTicket ticket,
        ProcessState state,
        int exitCode,
        List<ProcessOutput> logs,
        long nextLogSequence)
    {
        Ticket = ticket;
        State = state;
        ExitCode = exitCode;
        Logs = logs;
        NextLogSequence = nextLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public List<ProcessOutput> Logs { get; }

    public long NextLogSequence { get; }

    public ProcessState State { get; }

    public int ExitCode { get; }
}

[DebuggerDisplay("{Occurred} | {Source} | {Text}")]
public class ProcessOutput
{
    public ProcessOutput(ProcessOutputSource source, string text) : this(source, text, DateTimeOffset.UtcNow)
    {
    }

    [JsonConstructor]
    public ProcessOutput(ProcessOutputSource source, string text, DateTimeOffset occurred)
    {
        Source = source;
        Text = text;
        Occurred = occurred;
    }

    public ProcessOutputSource Source { get; }

    public DateTimeOffset Occurred { get; }

    public string Text { get; }
}

public enum ProcessOutputSource
{
    StdOut,
    StdErr,
    Debug
}

public enum ProcessState
{
    Pending,
    Running,
    Complete
}

public class UploadResult
{
    public UploadResult(string fullPath, string hash, long length)
    {
        FullPath = fullPath;
        Hash = hash;
        Length = length;
    }

    public string FullPath { get; }

    public string Hash { get; }

    public long Length { get; }
}

public class CompleteScriptCommand
{
    public CompleteScriptCommand(ScriptTicket ticket, long lastLogSequence)
    {
        Ticket = ticket;
        LastLogSequence = lastLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public long LastLogSequence { get; }
}