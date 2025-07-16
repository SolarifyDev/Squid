using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Core.Services.Deployments.Machine
{
    public class MachineService : IMachineService
    {
        private readonly IMachineDataProvider _machineDataProvider; 
        private readonly IMapper _mapper;

        public MachineService(IMachineDataProvider machineDataProvider, IMapper mapper) 
        {
            _machineDataProvider = machineDataProvider; 
            _mapper = mapper;
        }

        public async Task<Guid> CreateMachineAsync(CreateMachineCommand command)
        {
            var entity = _mapper.Map<Message.Domain.Deployments.Machine>(command);
            entity.Id = Guid.NewGuid();
            await _machineDataProvider.AddMachineAsync(entity); 
            return entity.Id;
        }

        public async Task<bool> UpdateMachineAsync(UpdateMachineCommand command)
        {
            var entity = await _machineDataProvider.GetMachineByIdAsync(command.Id); 
            if (entity == null) return false;
            _mapper.Map(command, entity);
            await _machineDataProvider.UpdateMachineAsync(entity); 
            return true;
        }

        public async Task<bool> DeleteMachineAsync(Guid id)
        {
            var entity = await _machineDataProvider.GetMachineByIdAsync(id); 
            if (entity == null) return false;
            await _machineDataProvider.DeleteMachineAsync(entity); 
            return true;
        }

        public async Task<PaginatedResponse<MachineDto>> GetMachinesAsync(GetMachinesRequest request)
        {
            var items = await _machineDataProvider.GetMachinesAsync(request.Name, request.PageIndex, request.PageSize); 
            var total = await _machineDataProvider.GetMachinesCountAsync(request.Name); 
            var dtos = _mapper.Map<List<MachineDto>>(items);
            return new PaginatedResponse<MachineDto>(dtos, total);
        }
    }
} 