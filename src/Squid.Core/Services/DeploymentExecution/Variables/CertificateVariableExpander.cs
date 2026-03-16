using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

public interface ICertificateVariableExpander : IScopedDependency
{
    Task ExpandAsync(List<VariableDto> variables, CancellationToken ct);
}

public class CertificateVariableExpander : ICertificateVariableExpander
{
    private readonly ICertificateDataProvider _certificateDataProvider;

    public CertificateVariableExpander(ICertificateDataProvider certificateDataProvider)
    {
        _certificateDataProvider = certificateDataProvider;
    }

    public async Task ExpandAsync(List<VariableDto> variables, CancellationToken ct)
    {
        var certVars = variables
            .Where(v => v.Type == VariableType.Certificate && int.TryParse(v.Value, out _))
            .ToList();

        if (certVars.Count == 0) return;

        var certIds = certVars
            .Select(v => int.Parse(v.Value))
            .Distinct()
            .ToList();

        var certs = await _certificateDataProvider.GetCertificatesByIdsAsync(certIds, ct).ConfigureAwait(false);

        var certsById = certs.ToDictionary(c => c.Id);
        var subVars = new List<VariableDto>();

        foreach (var certVar in certVars)
        {
            var certId = int.Parse(certVar.Value);

            if (!certsById.TryGetValue(certId, out var cert)) continue;

            subVars.Add(new VariableDto { Name = certVar.Name + SpecialVariables.Certificate.ThumbprintSuffix, Value = cert.Thumbprint ?? string.Empty });
            subVars.Add(new VariableDto { Name = certVar.Name + SpecialVariables.Certificate.SubjectCommonNameSuffix, Value = cert.SubjectCommonName ?? string.Empty });
            subVars.Add(new VariableDto { Name = certVar.Name + SpecialVariables.Certificate.PfxSuffix, Value = cert.CertificateData ?? string.Empty, IsSensitive = true });
            subVars.Add(new VariableDto { Name = certVar.Name + SpecialVariables.Certificate.NotAfterSuffix, Value = cert.NotAfter.ToString("O") });
            subVars.Add(new VariableDto { Name = certVar.Name + SpecialVariables.Certificate.HasPrivateKeySuffix, Value = cert.HasPrivateKey.ToString() });
        }

        variables.AddRange(subVars);
    }
}
