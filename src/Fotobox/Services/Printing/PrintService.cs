// Stub – wird von Stream B implementiert
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Printing;

public sealed class PrintService : IPrintService
{
    public IReadOnlyList<PrinterInfo> GetPrinters() => [];

    public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName) => [];

    public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout) => PageSpec.Default;

    public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer, IProgress<string>? status = null, CancellationToken ct = default) =>
        Task.FromResult(new PrintResult(PrintOutcome.Completed));
}
