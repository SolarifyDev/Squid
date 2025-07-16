namespace Squid.Core.Services.Deployments.Machine
{
    public interface IMachineService : IScopedDependency
    {
        Task<Guid> CreateMachineAsync(CreateMachineCommand command);

        Task<bool> UpdateMachineAsync(UpdateMachineCommand command);

        Task<bool> DeleteMachineAsync(Guid id);

        Task<PaginatedResponse<MachineDto>> GetMachinesAsync(GetMachinesRequest request);
    }
} 