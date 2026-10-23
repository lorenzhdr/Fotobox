using System.Windows.Media;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>ComboBox-Einträge liefern per UI Automation <c>ToString()</c>: Es muss der Anzeigename sein.</summary>
public class OptionDisplayTests
{
    [Fact]
    public void CameraDevice_ToString_IsName() =>
        Assert.Equal("Kamera 1", new CameraDevice("id-1", "Kamera 1").ToString());

    [Fact]
    public void FontOption_ToString_IsDisplay() =>
        Assert.Equal("Beispielschrift", new FontOption("Example", "Beispielschrift", new FontFamily("Segoe UI")).ToString());

    [Fact]
    public void AdminOptions_ToString_IsDisplay()
    {
        Assert.Equal("Automatisch", new ResolutionOption(null, "Automatisch").ToString());
        Assert.Equal("Windows-Standarddrucker", new PrinterOption(null, "Windows-Standarddrucker").ToString());
        Assert.Equal("Standard des Druckers", new PaperOption(null, "Standard des Druckers").ToString());
        Assert.Equal("Hochformat", new OrientationOption(PageOrientation.Portrait, "Hochformat").ToString());
    }
}
