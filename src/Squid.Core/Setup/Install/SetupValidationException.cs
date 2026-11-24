namespace Squid.Core.Setup.Install;

/// <summary>The setup request is incomplete or invalid; <see cref="Errors"/> is keyed by field path (e.g. <c>admin.password</c>).</summary>
public class SetupValidationException : InvalidOperationException
{
    public SetupValidationException(IReadOnlyDictionary<string, string> errors) : base(string.Join("; ", errors.Values))
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string> Errors { get; }
}
