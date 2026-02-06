using LaciSynchroni.Common.Dto.Files;

namespace LaciSynchroni.WebAPI.Files.Models;

public class DownloadFileTransfer(DownloadFileDto dto, int serverIndex) : FileTransfer(dto, serverIndex)
{
    public override bool CanBeTransferred => Dto.FileExists && !Dto.IsForbidden && Dto.Size > 0;
    public Uri DownloadUri => new(Dto switch
    {
        { DirectDownloadUrl: { Length: > 0 } url } => url,
        { CDNDownloadUrl: { Length: > 0 } url } => url,
        _ => Dto.Url,
    });
    public bool IsDirectDownload => !string.IsNullOrEmpty(Dto.DirectDownloadUrl) || !string.IsNullOrEmpty(Dto.CDNDownloadUrl);
    public override long Total
    {
        set
        {
            // nothing to set
        }
        get => Dto.Size;
    }

    public long TotalRaw => Dto.RawSize;
    private DownloadFileDto Dto => (DownloadFileDto)TransferDto;
}
