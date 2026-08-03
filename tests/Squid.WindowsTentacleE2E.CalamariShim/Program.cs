// Calamari shim for E2E testing — records its invocation to a marker file.
//
// The DeployByCalamari.ps1 template resolves `squid-calamari.exe` and
// invokes it with `& $squidCalamari @commandArgs`.  This shim intercepts
// that call, writes a marker file so the test can prove:
//   • the shim was actually invoked (not a PATH-fallback miss)
//   • the resolved path was an absolute path (install-info.json sibling)
//   • the expected arguments were forwarded
//
// Usage: SQUID_CALAMARI_E2E_MARKER=<path> squid-calamari.exe [--marker=<path>] <calamari-args...>
//
// The environment variable keeps the marker mechanism separate from the
// production command contract. --marker remains backward-compatible for
// existing callers. Both are consumed by the shim and never forwarded to
// "calamari logic" (there is none — this is a test double). When absent the
// shim silently exits 0 so tests that don't need a marker still work.

var markerPath = Environment.GetEnvironmentVariable("SQUID_CALAMARI_E2E_MARKER");
var passthrough = new List<string>();

foreach (var arg in args)
{
    if (arg.StartsWith("--marker=", StringComparison.OrdinalIgnoreCase))
        markerPath = arg["--marker=".Length..];
    else
        passthrough.Add(arg);
}

if (markerPath is not null)
{
    var content = new System.Text.StringBuilder();
    content.AppendLine($"ProcessPath:{Environment.ProcessPath}");
    content.AppendLine($"Args:{string.Join("|", passthrough)}");
    content.AppendLine($"AllArgs:{string.Join("|", args)}");
    Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
    File.WriteAllText(markerPath, content.ToString());
}

return 0;
