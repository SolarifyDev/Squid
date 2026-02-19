using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Halibut;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Enums;
using Squid.Message.Models.Agent;

namespace Squid.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/agents")]
public class AgentController : ControllerBase
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly HalibutRuntime _halibutRuntime;
    private readonly SelfCertSetting _selfCertSetting;

    public AgentController(
        IRepository repository,
        IUnitOfWork unitOfWork,
        HalibutRuntime halibutRuntime,
        SelfCertSetting selfCertSetting)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _halibutRuntime = halibutRuntime;
        _selfCertSetting = selfCertSetting;
    }

    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(AgentRegistrationResponse))]
    public async Task<IActionResult> RegisterAsync(
        [FromBody] AgentRegistrationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Thumbprint))
            return BadRequest("Thumbprint is required");

        if (string.IsNullOrEmpty(request.SubscriptionId))
            return BadRequest("SubscriptionId is required");

        _halibutRuntime.Trust(request.Thumbprint);

        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            request.SubscriptionId,
            request.Thumbprint,
            request.Namespace
        });

        var machine = new Machine
        {
            Name = request.MachineName ?? $"k8s-agent-{request.SubscriptionId[..8]}",
            IsDisabled = false,
            Roles = request.Roles ?? string.Empty,
            EnvironmentIds = request.EnvironmentIds ?? string.Empty,
            Json = string.Empty,
            Thumbprint = request.Thumbprint,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = request.SpaceId,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            LicenseHash = string.Empty,
            Slug = $"k8s-agent-{Guid.NewGuid():N}",
            PollingSubscriptionId = request.SubscriptionId
        };

        await _repository.InsertAsync(machine, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var serverThumbprint = GetServerThumbprint();
        var subscriptionUri = $"poll://{request.SubscriptionId}/";

        return Ok(new AgentRegistrationResponse
        {
            MachineId = machine.Id,
            ServerThumbprint = serverThumbprint,
            SubscriptionUri = subscriptionUri
        });
    }

    private string GetServerThumbprint()
    {
        var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
        using var cert = new X509Certificate2(certBytes, _selfCertSetting.Password);

        return cert.Thumbprint;
    }
}
