using LaciSynchroni.Common.Data;
using LaciSynchroni.FileCache;
using LaciSynchroni.Services.CharaData.Models;

namespace LaciSynchroni.Services.CharaData;

public sealed class CharaFileDataFactory(FileCacheManager fileCacheManager)
{
    private readonly FileCacheManager _fileCacheManager = fileCacheManager;

    public CharaFileData Create(string description, CharacterData characterCacheDto)
    {
        return new CharaFileData(_fileCacheManager, description, characterCacheDto);
    }
}
