using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Api.Controllers;

public sealed class OctopusImportUploadLimitConvention : IActionModelConvention
{
    private readonly long _maxUploadSizeBytes;

    public OctopusImportUploadLimitConvention(OctopusArchiveExtractionOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.EnsureValid();
        _maxUploadSizeBytes = limits.MaxUploadSizeBytes;
    }

    public void Apply(ActionModel action)
    {
        if (action.Controller.ControllerType.AsType() != typeof(OctopusImportController)
            || !string.Equals(action.ActionMethod.Name, nameof(OctopusImportController.UploadAsync), StringComparison.Ordinal))
        {
            return;
        }

        action.Filters.Add(new RequestSizeLimitAttribute(_maxUploadSizeBytes));
        action.Filters.Add(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = _maxUploadSizeBytes
        });
    }
}
