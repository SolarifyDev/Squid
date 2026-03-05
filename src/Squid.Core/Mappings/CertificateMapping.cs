using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Certificate;

namespace Squid.Core.Mappings;

public class CertificateMapping : Profile
{
    public CertificateMapping()
    {
        CreateMap<Certificate, CertificateDto>()
            .ForMember(x => x.SubjectAlternativeNames, x => x.MapFrom(y =>
                string.IsNullOrEmpty(y.SubjectAlternativeNames) ? new List<string>()
                : y.SubjectAlternativeNames.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()))
            .ForMember(x => x.EnvironmentIds, x => x.MapFrom(y =>
                string.IsNullOrEmpty(y.EnvironmentIds) ? new List<int>()
                : DeserializeIds(y.EnvironmentIds)));
    }

    private static List<int> DeserializeIds(string json) => JsonSerializer.Deserialize<List<int>>(json);
}
