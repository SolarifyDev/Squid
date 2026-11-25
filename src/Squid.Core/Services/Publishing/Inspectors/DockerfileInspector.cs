using System.Globalization;
using System.Text.RegularExpressions;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing.Inspectors;

/// <summary>The first TCP port the Dockerfile's <c>EXPOSE</c> lines announce (<c>8080</c>, <c>8080/tcp</c>, the start of <c>8000-8010</c>), recorded as <see cref="PackageFactKeys.DockerfileExposePort"/>. Absent when there is none.</summary>
public sealed class DockerfileInspector : IPackageInspector
{
    private static readonly Regex ExposeLine = new(@"^\s*EXPOSE\s+(?<ports>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TcpPort = new(@"^(?<port>\d{1,5})(-\d{1,5})?(/tcp)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
    {
        var text = await context.TryReadTextAsync("Dockerfile", ct).ConfigureAwait(false);

        var port = text == null ? null : FirstExposedPort(text);

        if (port != null) context.Set(PackageFactKeys.DockerfileExposePort, port);
    }

    private static string FirstExposedPort(string dockerfile)
    {
        var tokens = ExposeLine.Matches(dockerfile).SelectMany(m => m.Groups["ports"].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        foreach (var token in tokens)
        {
            var match = TcpPort.Match(token);

            if (match.Success && IsPort(match.Groups["port"].Value)) return match.Groups["port"].Value;
        }

        return null;
    }

    private static bool IsPort(string text) => int.Parse(text, CultureInfo.InvariantCulture) is >= 1 and <= 65535;
}
