using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Default <see cref="ICapabilityValidator"/> implementation. Each individual check runs
/// independently and contributes zero or more <see cref="CapabilityViolation"/> records to
/// a single accumulator list so the caller sees every failure in one pass.
///
/// <para>
/// The checks are intentionally additive and order-independent — adding a new check must
/// never cause an existing check to be skipped. The shape of the checks also stays aligned
/// with the shape of <see cref="ITransportCapabilities"/>: each capability field either has
/// a corresponding check here or is intentionally ignored (see XML docs on each method).
/// </para>
/// </summary>
public sealed class CapabilityValidator : ICapabilityValidator
{
    public IReadOnlyList<CapabilityViolation> Validate(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        string? actionType = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(capabilities);

        var violations = new List<CapabilityViolation>();

        ValidateActionType(intent, capabilities, communicationStyle, actionType, violations);
        ValidateScriptSyntax(intent, capabilities, communicationStyle, violations);
        ValidateNestedFiles(intent, capabilities, communicationStyle, violations);
        ValidateRequiredFeatures(intent, capabilities, communicationStyle, violations);
        ValidatePackageStaging(intent, capabilities, communicationStyle, violations);

        return violations;
    }

    /// <summary>
    /// Emits <see cref="ViolationCodes.UnsupportedActionType"/> when the caller passes a
    /// non-null <paramref name="actionType"/>, the transport declares a non-empty
    /// <see cref="ITransportCapabilities.SupportedActionTypes"/>, and the action type is
    /// not a member of that set. Transports that leave <c>SupportedActionTypes</c> empty
    /// are treated as "accepts any" and never generate this violation.
    /// </summary>
    private static void ValidateActionType(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        string? actionType,
        List<CapabilityViolation> violations)
    {
        if (string.IsNullOrEmpty(actionType)) return;
        if (capabilities.SupportedActionTypes.Count == 0) return;
        if (capabilities.SupportedActionTypes.Contains(actionType)) return;

        violations.Add(BuildViolation(
            ViolationCodes.UnsupportedActionType,
            $"Transport does not support action type '{actionType}'.",
            intent,
            communicationStyle,
            actionType));
    }

    /// <summary>
    /// Checks script-bearing intents against the transport's
    /// <see cref="ITransportCapabilities.SupportedSyntaxes"/>. Currently covers
    /// <see cref="RunScriptIntent"/> and <see cref="DeployPackageIntent"/>; other intents
    /// don't carry a script syntax and are skipped.
    /// </summary>
    private static void ValidateScriptSyntax(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        List<CapabilityViolation> violations)
    {
        var syntax = ExtractSyntax(intent);
        if (syntax is null) return;
        if (capabilities.SupportedSyntaxes.Count == 0) return;
        if (capabilities.SupportedSyntaxes.Contains(syntax.Value)) return;

        violations.Add(BuildViolation(
            ViolationCodes.UnsupportedSyntax,
            $"Transport does not support script syntax '{syntax.Value}'.",
            intent,
            communicationStyle,
            syntax.Value.ToString()));
    }

    /// <summary>
    /// Returns the nullable <see cref="ScriptSyntax"/> embedded in <paramref name="intent"/>
    /// if the intent carries a script body. Intent types without a script syntax return null.
    /// </summary>
    private static ScriptSyntax? ExtractSyntax(ExecutionIntent intent) => intent switch
    {
        RunScriptIntent rs => rs.Syntax,
        DeployPackageIntent dp => dp.ScriptSyntax,
        HealthCheckIntent hc when !string.IsNullOrEmpty(hc.CustomScript) => hc.Syntax,
        _ => null
    };

    /// <summary>
    /// Emits <see cref="ViolationCodes.NestedFiles"/> once for every asset whose
    /// <see cref="DeploymentFile.RelativePath"/> contains a directory separator when the
    /// transport has <c>SupportsNestedFiles == false</c>. Each offending path becomes its
    /// own violation so the preview UI can list them individually.
    /// </summary>
    private static void ValidateNestedFiles(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        List<CapabilityViolation> violations)
    {
        if (capabilities.SupportsNestedFiles) return;

        foreach (var asset in intent.Assets)
        {
            if (!IsNestedPath(asset.RelativePath)) continue;

            violations.Add(BuildViolation(
                ViolationCodes.NestedFiles,
                $"Transport does not support nested files; asset '{asset.RelativePath}' has a directory separator.",
                intent,
                communicationStyle,
                asset.RelativePath));
        }
    }

    private static bool IsNestedPath(string relativePath) =>
        relativePath.Contains('/') || relativePath.Contains('\\');

    /// <summary>
    /// Emits <see cref="ViolationCodes.MissingFeature"/> once per required capability key
    /// that is not present in <see cref="ITransportCapabilities.OptionalFeatures"/>.
    /// Case-insensitive match; empty requirements short-circuit.
    /// </summary>
    private static void ValidateRequiredFeatures(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        List<CapabilityViolation> violations)
    {
        if (intent.RequiredCapabilities.Count == 0) return;

        foreach (var key in intent.RequiredCapabilities)
        {
            if (capabilities.OptionalFeatures.Contains(key)) continue;

            violations.Add(BuildViolation(
                ViolationCodes.MissingFeature,
                $"Transport does not declare required feature '{key}'.",
                intent,
                communicationStyle,
                key));
        }
    }

    /// <summary>
    /// Emits <see cref="ViolationCodes.PackageStaging"/> when the intent declares one or
    /// more <see cref="IntentPackageReference"/> entries (either via
    /// <see cref="ExecutionIntent.Packages"/> or a <see cref="DeployPackageIntent"/>) but
    /// the transport's <see cref="ITransportCapabilities.PackageStagingModes"/> is
    /// <see cref="PackageStagingMode.None"/>.
    /// </summary>
    private static void ValidatePackageStaging(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        List<CapabilityViolation> violations)
    {
        if (!RequiresPackageStaging(intent)) return;
        if (capabilities.PackageStagingModes != PackageStagingMode.None) return;

        violations.Add(BuildViolation(
            ViolationCodes.PackageStaging,
            "Transport does not support package staging but the intent declares one or more packages.",
            intent,
            communicationStyle,
            detail: null));
    }

    private static bool RequiresPackageStaging(ExecutionIntent intent)
    {
        if (intent.Packages.Count > 0) return true;
        if (intent is DeployPackageIntent) return true;

        return false;
    }

    private static CapabilityViolation BuildViolation(
        string code,
        string message,
        ExecutionIntent intent,
        CommunicationStyle communicationStyle,
        string? detail) => new()
    {
        Code = code,
        Message = message,
        CommunicationStyle = communicationStyle,
        IntentName = intent.Name,
        StepName = intent.StepName,
        ActionName = intent.ActionName,
        Detail = detail
    };
}
