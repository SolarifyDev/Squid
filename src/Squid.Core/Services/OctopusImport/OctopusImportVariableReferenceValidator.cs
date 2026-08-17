using System.Reflection;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.VariableSubstitution.Templates;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportVariableReferenceValidator : IScopedDependency
{
    OctopusImportValidationResultDto Validate(OctopusResourceGraph graph);
}

public class OctopusImportVariableReferenceValidator : IOctopusImportVariableReferenceValidator
{
    private static readonly IReadOnlyDictionary<string, string> OctopusSystemVariableEquivalents =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Project.Id"] = SpecialVariables.Project.Id,
            ["Octopus.Project.Name"] = SpecialVariables.Project.Name,
            ["Octopus.Environment.Id"] = SpecialVariables.Environment.Id,
            ["Octopus.Environment.Name"] = SpecialVariables.Environment.Name,
            ["Octopus.Machine.Id"] = SpecialVariables.Machine.Id,
            ["Octopus.Machine.Name"] = SpecialVariables.Machine.Name,
            ["Octopus.Machine.Roles"] = SpecialVariables.Machine.Roles,
            ["Octopus.Deployment.Id"] = SpecialVariables.Deployment.Id,
            ["Octopus.Release.Number"] = SpecialVariables.Release.Number,
            ["Octopus.Action.Package.PackageId"] = SpecialVariables.Action.PackageId,
            ["Octopus.Action.Package.FeedId"] = SpecialVariables.Action.PackageFeedId,
            ["Octopus.Action.Package.PackageVersion"] = SpecialVariables.Action.PackageVersion
        };

    private static readonly IReadOnlySet<string> SquidSystemVariableNames = BuildSquidSystemVariableNames();

    public OctopusImportValidationResultDto Validate(OctopusResourceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var result = new OctopusImportValidationResultDto();
        var variableDefinitions = GetVariableDefinitions(graph);
        var references = GetReferenceSources(graph)
            .SelectMany(source => ExtractReferences(source.Value).Select(reference => new VariableReference(source, reference)))
            .ToList();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in references.OrderBy(r => r.Source.ResourceKind).ThenBy(r => r.Source.SourceId, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal))
            ValidateReference(reference, variableDefinitions, result, emitted);

        return result;
    }

    private static VariableDefinitionLookup GetVariableDefinitions(OctopusResourceGraph graph)
    {
        var definitions = graph.Resources
            .Where(r => r.Kind == OctopusResourceKind.VariableSet && !r.IsHistorical)
            .Select(r => r.GetSource<OctopusVariableSetDto>())
            .Where(vs => vs != null)
            .SelectMany(vs => vs.Variables ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => v.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new VariableDefinitionLookup(definitions);
    }

    private static IEnumerable<VariableReferenceSource> GetReferenceSources(OctopusResourceGraph graph)
    {
        foreach (var variableSetResource in graph.Resources.Where(r => r.Kind == OctopusResourceKind.VariableSet && !r.IsHistorical))
        {
            var variableSet = variableSetResource.GetSource<OctopusVariableSetDto>();

            if (variableSet?.Variables == null)
                continue;

            foreach (var variable in variableSet.Variables)
            {
                yield return new VariableReferenceSource(OctopusResourceKind.Variable, variable.Id, variable.Name, "Variable.Value", variable.Value);
                yield return new VariableReferenceSource(OctopusResourceKind.Variable, variable.Id, variable.Name, "Variable.Description", variable.Description);
                yield return new VariableReferenceSource(OctopusResourceKind.Variable, variable.Id, variable.Name, "Variable.Prompt.Label", variable.Prompt?.Label);
                yield return new VariableReferenceSource(OctopusResourceKind.Variable, variable.Id, variable.Name, "Variable.Prompt.Description", variable.Prompt?.Description);
            }
        }

        foreach (var processResource in graph.Resources.Where(r => r.Kind == OctopusResourceKind.DeploymentProcess && !r.IsHistorical))
        {
            var process = processResource.GetSource<OctopusDeploymentProcessDto>();

            if (process?.Steps == null)
                continue;

            foreach (var step in process.Steps)
            {
                yield return new VariableReferenceSource(OctopusResourceKind.DeploymentStep, step.Id, step.Name, "Step.Condition", step.Condition);
                yield return new VariableReferenceSource(OctopusResourceKind.DeploymentStep, step.Id, step.Name, "Step.StartTrigger", step.StartTrigger);
                yield return new VariableReferenceSource(OctopusResourceKind.DeploymentStep, step.Id, step.Name, "Step.PackageRequirement", step.PackageRequirement);

                foreach (var property in step.Properties ?? [])
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentStep, step.Id, step.Name, $"Step.Properties.{property.Key}", property.Value);

                foreach (var action in step.Actions ?? [])
                {
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.WorkerPoolVariable", action.WorkerPoolVariable);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.EnvironmentsVariable", action.EnvironmentsVariable);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.ExcludedEnvironmentsVariable", action.ExcludedEnvironmentsVariable);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.ChannelsVariable", action.ChannelsVariable);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.TenantTagsVariable", action.TenantTagsVariable);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.Condition", action.Condition);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.Container.Image", action.Container?.Image);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.Container.GitUrl", action.Container?.GitUrl);
                    yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, "Action.Container.Dockerfile", action.Container?.Dockerfile);

                    foreach (var property in action.Properties ?? [])
                        yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, $"Action.Properties.{property.Key}", property.Value);

                    foreach (var package in action.Packages ?? [])
                    {
                        yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, $"Action.Packages.{package.Id}.PackageId", package.PackageId);
                        yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, $"Action.Packages.{package.Id}.Version", package.Version);

                        foreach (var property in package.Properties ?? [])
                            yield return new VariableReferenceSource(OctopusResourceKind.DeploymentAction, action.Id, action.Name, $"Action.Packages.{package.Id}.Properties.{property.Key}", property.Value);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ExtractReferences(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains("#{", StringComparison.Ordinal))
            return [];

        if (!TemplateParser.TryParseTemplate(value, out var template, out _, haltOnError: false))
            return [];

        var references = new List<string>();
        CollectReferences(template.Tokens, references);

        return references
            .Select(NormalizeReferenceName)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal);
    }

    private static void CollectReferences(IEnumerable<TemplateToken> tokens, List<string> references)
    {
        foreach (var token in tokens)
        {
            switch (token)
            {
                case SubstitutionToken substitution:
                    CollectReferences(substitution.Expression, references);
                    break;
                case ConditionalToken conditional:
                    CollectReferences(conditional.Token.LeftSide, references);

                    if (conditional.Token is ConditionalSymbolExpressionToken symbolCondition)
                        CollectReferences(symbolCondition.RightSide, references);

                    CollectReferences(conditional.TruthyTemplate, references);
                    CollectReferences(conditional.FalsyTemplate, references);
                    break;
                case RepetitionToken repetition:
                    CollectReferences(repetition.Collection, references);
                    CollectReferences(repetition.Template, references);
                    break;
            }
        }
    }

    private static void CollectReferences(ContentExpression expression, List<string> references)
    {
        switch (expression)
        {
            case SymbolExpression symbol:
                references.Add(symbol.ToString());
                break;
            case FunctionCallExpression function:
                CollectReferences(function.Argument, references);
                CollectReferences(function.Options, references);
                break;
        }
    }

    private static string NormalizeReferenceName(string reference)
    {
        var normalized = reference?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var bracketIndex = normalized.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex > 0)
            normalized = normalized[..bracketIndex];

        return normalized.Trim();
    }

    private static void ValidateReference(
        VariableReference reference,
        VariableDefinitionLookup variableDefinitions,
        OctopusImportValidationResultDto result,
        HashSet<string> emitted)
    {
        if (variableDefinitions.ContainsExact(reference.Name) || SquidSystemVariableNames.Contains(reference.Name))
            return;

        if (variableDefinitions.TryGetCaseInsensitive(reference.Name, out var definedName))
        {
            AddDiagnostic(
                result,
                emitted,
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportVariableReferenceDiagnosticCodes.CaseOnlyVariableMismatch,
                $"Octopus variable reference '#{{{reference.Name}}}' differs only by case from imported variable '{definedName}'.",
                reference);
            return;
        }

        if (OctopusSystemVariableEquivalents.TryGetValue(reference.Name, out var equivalent))
        {
            AddDiagnostic(
                result,
                emitted,
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportVariableReferenceDiagnosticCodes.SystemVariableEquivalent,
                $"Octopus system variable '#{{{reference.Name}}}' should use Squid equivalent '#{{{equivalent}}}'.",
                reference);
            return;
        }

        if (reference.Name.StartsWith("Octopus.", StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic(
                result,
                emitted,
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportVariableReferenceDiagnosticCodes.UnsupportedOctopusSystemVariable,
                $"Octopus system variable '#{{{reference.Name}}}' does not have a known Squid equivalent.",
                reference);
            return;
        }

        AddDiagnostic(
            result,
            emitted,
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition,
            $"Octopus variable reference '#{{{reference.Name}}}' has no imported variable definition.",
            reference);
    }

    private static void AddDiagnostic(
        OctopusImportValidationResultDto result,
        HashSet<string> emitted,
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        VariableReference reference)
    {
        var key = $"{code}|{reference.Source.ResourceKind}|{reference.Source.SourceId}|{reference.Name}";
        if (!emitted.Add(key))
            return;

        result.Diagnostics.Add(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = $"{message} Source location: {reference.Source.Location}.",
            ResourceType = reference.Source.ResourceKind.ToString(),
            SourceId = reference.Source.SourceId,
            ResourceName = reference.Source.ResourceName
        });
    }

    private static IReadOnlySet<string> BuildSquidSystemVariableNames()
    {
        return typeof(SpecialVariables)
            .GetNestedTypes(BindingFlags.Public)
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => f.GetRawConstantValue() as string)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class VariableDefinitionLookup
    {
        private readonly HashSet<string> _exact;
        private readonly Dictionary<string, string> _caseInsensitive;

        public VariableDefinitionLookup(IEnumerable<string> names)
        {
            var nameList = names.ToList();
            _exact = nameList.ToHashSet(StringComparer.Ordinal);
            _caseInsensitive = nameList
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        public bool ContainsExact(string name) => _exact.Contains(name);

        public bool TryGetCaseInsensitive(string name, out string definedName)
            => _caseInsensitive.TryGetValue(name, out definedName);
    }

    private sealed record VariableReferenceSource(
        OctopusResourceKind ResourceKind,
        string SourceId,
        string ResourceName,
        string Location,
        string Value);

    private sealed record VariableReference(VariableReferenceSource Source, string Name);
}
