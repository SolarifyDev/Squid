namespace Squid.Message.Commands.Deployments.Machine
{
    public class DeleteMachineCommand : ICommand<DeleteMachineResponse> 
    { 
        public Guid Id { get; set; } 
    } 

    public class DeleteMachineResponse : IResponse 
    { 
        public bool Success { get; set; } 
    } 
} 