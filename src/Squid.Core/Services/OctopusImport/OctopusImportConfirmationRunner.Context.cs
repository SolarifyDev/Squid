using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed partial class OctopusImportConfirmationRunner
{
    private sealed class RunContext(Guid sessionId, int spaceId, int ownerUserId, byte[] admittedDataVersion = null)
    {
        // Request
        public Guid SessionId { get; } = sessionId;
        public int SpaceId { get; } = spaceId;
        public int OwnerUserId { get; } = ownerUserId;
        public byte[] AdmittedDataVersion { get; } = admittedDataVersion;

        // Session
        public OctopusImportSession Session { get; set; }
        public OctopusImportSessionState State { get; set; }
        public OctopusImportValidatedPlanDto ValidatedPlan { get; set; }
        public string SourcePath { get; set; }

        // Plan
        public OctopusImportPlanningSnapshot Snapshot { get; set; }

        // Outcome
        public OctopusImportSessionDto Confirmed { get; set; }
    }
}
