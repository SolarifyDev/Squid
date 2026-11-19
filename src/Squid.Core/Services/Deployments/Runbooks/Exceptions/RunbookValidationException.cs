namespace Squid.Core.Services.Deployments.Runbooks.Exceptions;

public class RunbookValidationException : InvalidOperationException
{
    public RunbookValidationException(string code, string field, string message)
        : base(message)
    {
        Code = code;
        Field = field;
    }

    public string Code { get; }

    public string Field { get; }
}

public sealed class RunbookNameConflictException : RunbookValidationException
{
    public RunbookNameConflictException(string name)
        : base(
            "runbook.name_conflict",
            nameof(name),
            $"A Runbook named '{name}' already exists in this project.")
    {
    }
}
