using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Packages.BuiltIn;

public sealed partial class BuiltInPackagePushService
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

        // Inspection
        public PackageFacts Facts { get; set; }

        // Version
        public string Version { get; set; }
        public BuiltInPackage Replacing { get; set; }

        // Storage
        public int StoreId { get; set; }
        public IPackageBlobStore Store { get; set; }
        public string BlobKey { get; set; }

        // Result
        public BuiltInPackage Recorded { get; set; }
    }
}
