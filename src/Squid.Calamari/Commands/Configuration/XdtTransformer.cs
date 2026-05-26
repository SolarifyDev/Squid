using System.Xml;
using Microsoft.Web.XmlTransform;
using Squid.Calamari.Commands.Common;

namespace Squid.Calamari.Commands.Configuration;

/// <summary>
/// G1.2 — thin wrapper over Microsoft's <c>Microsoft.Web.XmlTransform</c>
/// XDT engine. Pure-function API: takes paths, returns a result record.
/// All exception handling centralised here so the pipeline step orchestrator
/// stays focused on the loop + logging.
///
/// <para><b>Why not use XmlTransformableDocument directly</b>: the raw API
/// throws <c>XmlException</c> on malformed input, leaves partial writes on
/// I/O errors, and requires the caller to manage encoding manually. This
/// wrapper:
/// <list type="bullet">
///   <item>Returns a typed <see cref="TransformResult"/> instead of throwing
///         for predictable failure modes (missing file, malformed XML)</item>
///   <item>Writes through a temp file + atomic rename so partial transforms
///         don't leave a corrupted base file on disk</item>
///   <item>Preserves the original XML declaration + encoding</item>
/// </list></para>
/// </summary>
internal static class XdtTransformer
{
    public static TransformResult Transform(string basePath, string transformPath)
    {
        if (!File.Exists(basePath))
            return TransformResult.Failure($"Base file '{basePath}' does not exist; cannot apply XDT transform.");
        if (!File.Exists(transformPath))
            return TransformResult.Failure($"Transform file '{transformPath}' does not exist; nothing to apply.");

        XmlTransformableDocument document;
        try
        {
            document = new XmlTransformableDocument { PreserveWhitespace = true };
            document.Load(basePath);
        }
        catch (XmlException ex)
        {
            return TransformResult.Failure($"Base file '{basePath}' is not well-formed XML: {ex.Message}");
        }
        catch (IOException ex)
        {
            return TransformResult.Failure($"Failed to read base file '{basePath}': {ex.Message}");
        }

        XmlTransformation transformation;
        try
        {
            transformation = new XmlTransformation(transformPath);
        }
        catch (XmlException ex)
        {
            document.Dispose();
            return TransformResult.Failure($"Transform file '{transformPath}' is not well-formed XML: {ex.Message}");
        }
        catch (IOException ex)
        {
            document.Dispose();
            return TransformResult.Failure($"Failed to read transform file '{transformPath}': {ex.Message}");
        }

        try
        {
            var applied = transformation.Apply(document);

            if (!applied)
            {
                // XDT engine returned false — usually means a transform directive
                // failed (e.g. Locator matched nothing). Continue but report.
                return TransformResult.Failure($"XDT engine declined to apply '{transformPath}' to '{basePath}'. " +
                                               "Check the transform's Locator + xdt:Transform attributes for missing matches.");
            }

            // Atomic write via the shared FileIO primitive: serialise to a
            // sibling temp first, then rename. Avoids a half-written base
            // config on disk if the writer dies mid-way (disk full, kill -9).
            var tempPath = basePath + EncodingPreservingFileIO.TempSuffix;
            document.Save(tempPath);
            File.Move(tempPath, basePath, overwrite: true);

            return TransformResult.Success();
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return TransformResult.Failure($"XDT transform failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            document.Dispose();
            transformation.Dispose();
        }
    }
}

internal sealed record TransformResult(bool Succeeded, string? FailureReason)
{
    public static TransformResult Success() => new(true, null);
    public static TransformResult Failure(string reason) => new(false, reason);
}
