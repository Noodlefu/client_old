using LaciSynchroni.Common.Dto.Files;

namespace LaciSynchroni.WebAPI.Files.Models;

public class UploadFileTransfer(UploadFileDto dto, int serverIndex) : FileTransfer(dto, serverIndex)
{
    public string LocalFile { get; set; } = string.Empty;
    public override long Total { get; set; }
}
