using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Packages.BuiltIn;

internal sealed partial class BuiltInPackagePushService
{
    private sealed class PushContext(PushBuiltInPackageCommand command)
    {
        // Request
        public PushBuiltInPackageCommand Command { get; } = command;
        public ExternalFeed Feed { get; set; }
        public string PackageId { get; set; }

        // Upload
        public StagedUpload Staged { get; set; }
        public BuiltInPackageFormat Format { get; set; }
        public PackageArchiveInspection Inspection { get; set; }

        // Inspection
        public PackageFacts Facts { get; set; }

        // Version
        public string Version { get; set; }
        public BuiltInPackage Replacing { get; set; }

        // Storage
        public StoredBlob Blob { get; set; }

        // Result
        public BuiltInPackage Recorded { get; set; }
    }
}
