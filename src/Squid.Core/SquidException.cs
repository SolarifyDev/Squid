namespace Squid.Core;

public class SquidException : Exception
{
    public SquidException()
    {
    }

    public SquidException(string message)
        : base(message)
    {
    }

    public SquidException(string messageFormat, params object[] args)
        : base(string.Format(messageFormat, args))
    {
    }

    public SquidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}