using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Machine
{
    public class UpdateMachineCommand : ICommand
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public bool IsDisabled { get; set; }
        public string Roles { get; set; }
        public string EnvironmentIds { get; set; }
        public string Json { get; set; }
        public Guid? MachinePolicyId { get; set; }
        public string Thumbprint { get; set; }
        public string Fingerprint { get; set; }
        public string DeploymentTargetType { get; set; }
        public Guid SpaceId { get; set; }
        public string OperatingSystem { get; set; }
        public string ShellName { get; set; }
        public string ShellVersion { get; set; }
        public string LicenseHash { get; set; }
        public string Slug { get; set; }
    }

    public class UpdateMachineResponse : SquidResponse<UpdateMachineResponseData>
    {
    }

    public class UpdateMachineResponseData
    {
        public MachineDto Machine { get; set; }
    }
}