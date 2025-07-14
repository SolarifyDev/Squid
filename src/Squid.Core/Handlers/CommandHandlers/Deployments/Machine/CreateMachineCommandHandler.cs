using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Events.Deployments.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Machine
{
    public class CreateMachineCommandHandler : ICommandHandler<CreateMachineCommand, CreateMachineResponse>
    {
        private readonly IMachineService _machineService;
        private readonly IEventBus _eventBus;

        public CreateMachineCommandHandler(IMachineService machineService, IEventBus eventBus)
        {
            _machineService = machineService;
            _eventBus = eventBus;
        }

        public async Task<CreateMachineResponse> Handle(IReceiveContext<CreateMachineCommand> context, CancellationToken cancellationToken)
        {
            var id = await _machineService.CreateMachineAsync(context.Message);
            await _eventBus.PublishAsync(new MachineCreatedEvent { Id = id, Name = context.Message.Name });
            return new CreateMachineResponse { Id = id };
        }
    }
} 