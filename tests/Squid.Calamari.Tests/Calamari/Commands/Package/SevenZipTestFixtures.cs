namespace Squid.Calamari.Tests.Calamari.Commands.Package;

/// <summary>
/// PR-11 — pre-built <c>.7z</c> fixtures (base64) for the SharpCompress
/// extractor tests.
///
/// <para><b>Why embedded base64 and not generated at test time</b>: 7z has
/// no managed WRITER — SharpCompress (the production dep) only reads 7z, and
/// the test runner is not assumed to have a <c>7z</c> CLI. So these archives
/// were generated offline (py7zr 1.0.0, LZMA2 default) and embedded here.
/// The tests decode them to a temp file and drive the REAL production
/// extractor against REAL 7z bytes — high fidelity, zero runtime tooling
/// dependency, runs identically on every dev box + CI.</para>
///
/// <para>The <see cref="Traversal"/> fixture deliberately contains a
/// <c>../../escape.txt</c> entry — a hostile archive that standard 7z tooling
/// REFUSES to create (py7zr's own path validator rejects it). It was built by
/// bypassing that validator, so the extractor's zip-slip defence can be tested
/// against a genuine malicious archive rather than a mock.</para>
/// </summary>
internal static class SevenZipTestFixtures
{
    /// <summary><c>readme.txt</c> = "hello from 7z", <c>bin/app.dll</c> = "fake-binary-content".</summary>
    public const string Happy =
        "N3q8ryccAASjuykwoAAAAAAAAAAVAAAAAAAAAJb+fA0BAB9oZWxsbyBmcm9tIDd6ZmFrZS1iaW5hcnktY29udGVudADgAIYAdF0AAIEzB64P0Es5PJ85EJxt+2pcRsiMI2VEGWxfJMkC53OZbK0MuohMV8sV1UVu1l7zcZTdNZ91Jq8ajotLCxB3PJKP8ZdmP3rChfIpPAa0svLe2zLLHHfp/GgzIj3ItX37IGvIzZTMrjFldLo50ctiATWqgwAAFwYkAQl8AAcLAQABISEBGAyAhwAA";

    /// <summary><c>ok.txt</c> = "fine", <c>../../escape.txt</c> = "should-not-write" (zip-slip).</summary>
    public const string Traversal =
        "N3q8ryccAATNE3U3jAAAAAAAAAAVAAAAAAAAAKoR/qEBABNmaW5lc2hvdWxkLW5vdC13cml0ZQDgAIgAbF0AAIEzB64Pz4CuDA/Ual595dffeE/G8XzVUpxooJogMr7Qa9JaQ78gn+C2IDV7PruGaI1wLGxYqd5Y0xMrLMj5blEEpVBe4w9eyZ7f9zTUXaedbxQO1zwIaELLsJ6O1p7rJ2Lw55u2w3KkTczcABcGGAEJdAAHCwEAASEhARgMgIkAAA==";

    /// <summary><c>big.bin</c> = 2 MiB of zeros (declared size 2,097,152) — trips a 1 MB per-entry cap.</summary>
    public const string Oversize =
        "N3q8ryccAAS8r/JI2gEAAAAAAAAVAAAAAAAAABsXDKz//xEBbF0AAG/9//+jt/9HPkgVcjlhUbiSKOajhgf57uQegtMvxTo8AUuxfsmKik0vow3Zf6bjjCMRU+BZGMV1iuJ3+LaUfwxqwN50SWTi6VxTsgTY90QMq18NbUbp5cN2iLeWV6y2TeFpHW/7S4gQbELLiD9cAI/QTq8mKJRxHz2PJOFwnqcjX+woy4XRlZiKfiqR8id19xnABphNmP3Yr9WQD8QlU/j1kTYxBaWw7m/BcE1HDNGREaqtYB26zrEnGFxZhulmUli+6XasWeTlWwUI+cfarfz7Uit0zR5bIEL53VM9+ClkCTuAyyps37U78MS9Ll+qDz5LZkKQEw7/EJP4cXhZ+AvN/5UoRg+p/Hze+5owLlbAj4Xzg4HAZcQlU/j1kTYxBaWw7m/BcE1HDNGREaqtYB26zrEnGFxZhulmUli+6XasWeTlWwUI+cfarfz7Uit0zR5bIEL53VM9+ClkCTuAyyps37U78MS8SCfmWIAA7QAFABymJPEAAOAAXABTXQAAgTMHrg/VOqJE1yTRz+P3ZNFayW/fgJpT3MF/s92ehqKhy0lzIvljFX6yU5lxibo7tCFnH5fyz+e01tAObcZGLC2l61IAgHGwCvcorz/yHwM2AAAXBoF/AQlbAAcLAQABISEBGAxdAAA=";

    /// <summary>Decode a base64 fixture to <paramref name="path"/> and return the path.</summary>
    public static string WriteToFile(string base64, string path)
    {
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }
}
