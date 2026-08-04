namespace Squid.Core.Services.OctopusImport.Octopus;

public class OctopusArchiveExtractionException : Exception
{
    public OctopusArchiveExtractionException(string code, string message, string sourcePath = null, Exception innerException = null)
        : base(message, innerException)
    {
        Code = code;
        SourcePath = sourcePath;
    }

    public string Code { get; }

    public string SourcePath { get; }
}
