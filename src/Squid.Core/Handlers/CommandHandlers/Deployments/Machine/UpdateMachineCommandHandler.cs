using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Events.Deployments.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Machine
{
    public class UpdateMachineCommandHandler : ICommandHandler<UpdateMachineCommand, UpdateMachineResponse>
    {
        private readonly IMachineService _machineService;
        private readonly IEventBus _eventBus;

        public UpdateMachineCommandHandler(IMachineService machineService, IEventBus eventBus)
        {
            _machineService = machineService;
            _eventBus = eventBus;
        }

        public async Task<UpdateMachineResponse> Handle(IReceiveContext<UpdateMachineCommand> context, CancellationToken cancellationToken)
        {
            var success = await _machineService.UpdateMachineAsync(context.Message);
            if (success)
            {
                await _eventBus.PublishAsync(new MachineUpdatedEvent { Id = context.Message.Id, Name = context.Message.Name });
            }
            return new UpdateMachineResponse { Success = success };
        }
    }
} 