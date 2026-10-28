using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Peace;

/// <summary>Verziert nichts: gibt das Foto unverändert zurück.</summary>
public sealed class NullPhotoDecorator : IPhotoDecorator
{
    public bool IsActive => false;

    public Task<BitmapSource> DecorateAsync(BitmapSource rawPhoto, int shotIndex, CancellationToken ct) => Task.FromResult(rawPhoto);
}
