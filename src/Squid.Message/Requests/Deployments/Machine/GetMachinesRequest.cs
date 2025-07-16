namespace Squid.Message.Requests.Deployments.Machine
{
    public class GetMachinesRequest : IPaginatedRequest
    {
        public string Name { get; set; }

        public int PageIndex { get; set; }

        public int PageSize { get; set; }
    }
} 