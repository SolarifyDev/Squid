using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Events.Deployments.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Machine
{
    public class DeleteMachineCommandHandler : ICommandHandler<DeleteMachineCommand, DeleteMachineResponse>
    {
        private readonly IMachineService _machineService;
        private readonly IEventBus _eventBus;

        public DeleteMachineCommandHandler(IMachineService machineService, IEventBus eventBus)
        {
            _machineService = machineService;
            _eventBus = eventBus;
        }

        public async Task<DeleteMachineResponse> Handle(IReceiveContext<DeleteMachineCommand> context, CancellationToken cancellationToken)
        {
            var success = await _machineService.DeleteMachineAsync(context.Message.Id);
            if (success)
            {
                await _eventBus.PublishAsync(new MachineDeletedEvent { Id = context.Message.Id });
            }
            return new DeleteMachineResponse { Success = success };
        }
    }
} 