// Stub – wird von Stream D implementiert
using System.IO;
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

public sealed class PhotoStorage : IPhotoStorage
{
    public string ResolveTargetFolder(StorageSettings storage, DateTime timestamp)
    {
        var root = string.IsNullOrWhiteSpace(storage.PhotoFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fotobox")
            : storage.PhotoFolder;
        return storage.DateSubfolder ? Path.Combine(root, timestamp.ToString("yyyy-MM-dd")) : root;
    }

    public Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals, StorageSettings storage, DateTime timestamp, CancellationToken ct = default) =>
        throw new NotImplementedException("Stub – wird von Stream D implementiert.");
}
