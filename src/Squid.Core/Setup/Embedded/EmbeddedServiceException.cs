namespace Squid.Core.Setup.Embedded;

/// <summary>A built-in PostgreSQL / Redis could not be initialised, started or stopped.</summary>
public class EmbeddedServiceException : InvalidOperationException
{
    public EmbeddedServiceException(string message) : base(message) { }
}
