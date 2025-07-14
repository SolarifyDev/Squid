namespace Squid.Message.Models.Deployments.Machine
{
    public class MachineDto : IBaseModel 
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

        public byte[] DataVersion { get; set; }

        public Guid SpaceId { get; set; }

        public string OperatingSystem { get; set; }

        public string ShellName { get; set; }

        public string ShellVersion { get; set; }

        public string LicenseHash { get; set; }

        public string Slug { get; set; }
    } 
} 