using Halibut;
using Halibut.ServiceModel;
using Squid.Core.Commands.Tentacle;

namespace Squid.Core.Services.Tentacle;


public interface IAsyncClientFileTransferService
{
    Task<UploadResult> UploadFileAsync(string remotePath, DataStream upload, HalibutProxyRequestOptions halibutProxyRequestOptions);
    
    Task<DataStream> DownloadFileAsync(string remotePath, HalibutProxyRequestOptions halibutProxyRequestOptions);
}