using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.PackageStorage;
using Squid.Message.Response;

namespace Squid.Message.Requests.PackageStorage;

[RequiresPermission(Permission.AdministerSystem)]
public class ListPackageStoresRequest : IRequest
{
}

public class ListPackageStoresResponse : SquidResponse<List<PackageStoreDto>>
{
}

[RequiresPermission(Permission.AdministerSystem)]
public class GetPackageStoreRequest : IRequest
{
    public int Id { get; set; }
}

public class GetPackageStoreResponse : SquidResponse<PackageStoreDto>
{
}

/// <summary>The kinds of package store this server can use and the settings each takes.</summary>
[RequiresPermission(Permission.AdministerSystem)]
public class ListPackageStoreProvidersRequest : IRequest
{
}

public class ListPackageStoreProvidersResponse : SquidResponse<List<PackageStoreProviderDescriptor>>
{
}
