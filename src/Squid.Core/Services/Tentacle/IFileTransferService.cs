using Halibut;

namespace Squid.Core.Services.Tentacle;

public interface IFileTransferService
{
    UploadResult UploadFile(string remotePath, DataStream upload);

    DataStream DownloadFile(string remotePath);
}
