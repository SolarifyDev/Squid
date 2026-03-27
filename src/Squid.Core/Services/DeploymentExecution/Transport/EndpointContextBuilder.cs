using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Serilog;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IEndpointContextBuilder : IScopedDependency
{
    Task<EndpointContext> BuildAsync(string endpointJson, IEndpointVariableContributor variables, CancellationToken ct);
}

public class EndpointContextBuilder(
    IDeploymentAccountDataProvider accountDataProvider,
    ICertificateDataProvider certificateDataProvider) : IEndpointContextBuilder
{
    public async Task<EndpointContext> BuildAsync(string endpointJson, IEndpointVariableContributor variables, CancellationToken ct)
    {
        var context = new EndpointContext { EndpointJson = endpointJson };

        var refs = variables.ParseResourceReferences(endpointJson);

        await ResolveAccountAsync(context, refs, ct).ConfigureAwait(false);
        await ResolveCertificatesAsync(context, refs, ct).ConfigureAwait(false);

        return context;
    }

    private async Task ResolveAccountAsync(EndpointContext context, Variables.EndpointResourceReferences refs, CancellationToken ct)
    {
        var accountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);
        if (!accountId.HasValue) return;

        var account = await accountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);

        if (account == null)
        {
            Log.Warning("Authentication account {AccountId} referenced in endpoint not found", accountId.Value);
            return;
        }

        context.SetAccountData(account.AccountType, account.Credentials);
    }

    private async Task ResolveCertificatesAsync(EndpointContext context, Variables.EndpointResourceReferences refs, CancellationToken ct)
    {
        foreach (var certRef in refs.References.Where(r => r.Type is EndpointResourceType.ClientCertificate or EndpointResourceType.ClusterCertificate))
        {
            var cert = await certificateDataProvider.GetCertificateByIdAsync(certRef.ResourceId, ct).ConfigureAwait(false);

            if (cert == null)
            {
                Log.Warning("Certificate {CertificateId} ({CertificateType}) referenced in endpoint not found", certRef.ResourceId, certRef.Type);
                continue;
            }

            context.SetCertificate(certRef.Type, DecodeCertificatePem(cert));

            if (certRef.Type == EndpointResourceType.ClientCertificate)
                EnrichCredentialsWithClientCertificate(context, cert);
        }
    }

    internal static void EnrichCredentialsWithClientCertificate(EndpointContext ctx, Certificate cert)
    {
        var accountData = ctx.GetAccountData() ?? new ResolvedAuthenticationAccountData { AuthenticationAccountType = AccountType.ClientCertificate };
        var creds = DeserializeOrCreateCredentials(accountData);

        var pemContent = DecodeCertificatePem(cert);
        creds.ClientCertificateData = pemContent;

        if (cert.HasPrivateKey)
            creds.ClientCertificateKeyData = DecodePrivateKeyPem(cert);

        ctx.SetAccountData(accountData.AuthenticationAccountType, DeploymentAccountCredentialsConverter.Serialize(creds));
    }

    private static ClientCertificateCredentials DeserializeOrCreateCredentials(ResolvedAuthenticationAccountData accountData)
    {
        if (accountData.AuthenticationAccountType == AccountType.ClientCertificate && !string.IsNullOrEmpty(accountData.CredentialsJson))
        {
            var existing = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson);

            if (existing is ClientCertificateCredentials cc) return cc;
        }

        return new ClientCertificateCredentials();
    }

    /// <summary>
    /// Decodes stored certificate data (base64 of raw file bytes) to PEM text.
    /// CertificateData in the DB is always base64(original bytes). The script template
    /// applies its own B64 encoding, so we must decode here to avoid double base64.
    /// </summary>
    internal static string DecodeCertificatePem(Certificate cert)
    {
        if (string.IsNullOrEmpty(cert.CertificateData)) return string.Empty;

        var bytes = Convert.FromBase64String(cert.CertificateData);

        return cert.CertificateDataFormat switch
        {
            CertificateDataFormat.Pem => Encoding.UTF8.GetString(bytes),
            CertificateDataFormat.Der => ConvertDerCertToPem(bytes),
            _ => ConvertPfxCertToPem(bytes, cert.Password)
        };
    }

    internal static string DecodePrivateKeyPem(Certificate cert)
    {
        if (string.IsNullOrEmpty(cert.CertificateData) || !cert.HasPrivateKey) return string.Empty;

        var bytes = Convert.FromBase64String(cert.CertificateData);

        return cert.CertificateDataFormat switch
        {
            // PEM file may contain both cert + key blocks — return full content
            CertificateDataFormat.Pem => Encoding.UTF8.GetString(bytes),
            CertificateDataFormat.Der => string.Empty,
            _ => ExportPfxPrivateKeyPem(bytes, cert.Password)
        };
    }

    private static string ConvertDerCertToPem(byte[] derBytes)
    {
        using var x509 = X509CertificateLoader.LoadCertificate(derBytes);
        return x509.ExportCertificatePem();
    }

    private static string ConvertPfxCertToPem(byte[] pfxBytes, string password)
    {
        using var x509 = X509CertificateLoader.LoadPkcs12(pfxBytes, password);
        return x509.ExportCertificatePem();
    }

    private static string ExportPfxPrivateKeyPem(byte[] pfxBytes, string password)
    {
        using var x509 = new X509Certificate2(pfxBytes, password, X509KeyStorageFlags.Exportable);

        using var rsa = x509.GetRSAPrivateKey();
        if (rsa != null)
            return new string(PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));

        using var ecdsa = x509.GetECDsaPrivateKey();
        if (ecdsa != null)
            return new string(PemEncoding.Write("PRIVATE KEY", ecdsa.ExportPkcs8PrivateKey()));

        return string.Empty;
    }
}
