namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Well-known violation codes returned by <see cref="ICapabilityValidator"/>.
///
/// <para>
/// Codes are stable, short, upper-snake strings so they can be logged, displayed in the
/// preview UI, and matched against in tests without leaking implementation detail. Every
/// code emitted by <see cref="CapabilityValidator"/> is defined here — drift tests assert
/// the generated violations are within <see cref="All"/>.
/// </para>
/// </summary>
public static class ViolationCodes
{
    /// <summary>The transport does not list the intent's action type in <c>SupportedActionTypes</c>.</summary>
    public const string UnsupportedActionType = "UNSUPPORTED_ACTION_TYPE";

    /// <summary>The transport does not list the intent's script syntax in <c>SupportedSyntaxes</c>.</summary>
    public const string UnsupportedSyntax = "UNSUPPORTED_SYNTAX";

    /// <summary>The intent ships assets with nested relative paths but the transport does not support nesting.</summary>
    public const string NestedFiles = "NESTED_FILES";

    /// <summary>The intent requires a capability key that is not declared in <c>OptionalFeatures</c>.</summary>
    public const string MissingFeature = "MISSING_FEATURE";

    /// <summary>The intent declares packages but the transport has <c>PackageStagingMode.None</c>.</summary>
    public const string PackageStaging = "PACKAGE_STAGING";

    /// <summary>The complete set of well-known violation codes. Used by drift tests.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        UnsupportedActionType,
        UnsupportedSyntax,
        NestedFiles,
        MissingFeature,
        PackageStaging
    };
}
