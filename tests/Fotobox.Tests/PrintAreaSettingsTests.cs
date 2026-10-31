using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Einstellungen „Druckbereich anzeigen“ (<see cref="PrintAreaSettings"/>): Standards, Klemmen, JSON, Einordnung beim Teilen.</summary>
public sealed class PrintAreaSettingsTests
{
    private static AppSettings Normalized(Action<AppSettings> configure)
    {
        var s = new AppSettings();
        configure(s);
        return SettingsNormalizer.Normalize(s);
    }

    [Fact]
    public void Defaults_AreOffWithShadedLinesInWhite()
    {
        var p = Normalized(_ => { }).PrintArea;

        Assert.False(p.Enabled);
        Assert.Equal(PrintAreaStyle.LinesShaded, p.Style);
        Assert.Equal(PrintAreaColor.White, p.Color);
        Assert.Equal(80, p.OpacityPercent);
        Assert.Equal(3, p.ThicknessPx);
        Assert.Equal(40, p.ShadePercent);
    }

    [Fact]
    public void Limits_AreTheDocumentedRanges_AndDefaultsLieInside()
    {
        Assert.Equal(80, SettingsLimits.PrintAreaOpacityDefault);
        Assert.InRange(SettingsLimits.PrintAreaOpacityDefault, SettingsLimits.PrintAreaOpacityMin, SettingsLimits.PrintAreaOpacityMax);
        Assert.Equal((1, 10), (SettingsLimits.PrintAreaThicknessMin, SettingsLimits.PrintAreaThicknessMax));
        Assert.InRange(SettingsLimits.PrintAreaThicknessDefault, SettingsLimits.PrintAreaThicknessMin, SettingsLimits.PrintAreaThicknessMax);
        Assert.InRange(SettingsLimits.PrintAreaShadeDefault, SettingsLimits.PrintAreaShadeMin, SettingsLimits.PrintAreaShadeMax);
    }

    [Theory]
    [InlineData(-5, SettingsLimits.PrintAreaOpacityMin)]
    [InlineData(0, SettingsLimits.PrintAreaOpacityMin)]
    [InlineData(1000, SettingsLimits.PrintAreaOpacityMax)]
    [InlineData(83, 85)]
    [InlineData(62, 60)]
    [InlineData(100, 100)]
    public void OpacityPercent_IsClampedAndRoundedToSteps(int input, int expected) =>
        Assert.Equal(expected, Normalized(s => s.PrintArea.OpacityPercent = input).PrintArea.OpacityPercent);

    [Theory]
    [InlineData(-3, SettingsLimits.PrintAreaThicknessMin)]
    [InlineData(0, SettingsLimits.PrintAreaThicknessMin)]
    [InlineData(7, 7)]
    [InlineData(11, SettingsLimits.PrintAreaThicknessMax)]
    public void ThicknessPx_IsClamped(int input, int expected) =>
        Assert.Equal(expected, Normalized(s => s.PrintArea.ThicknessPx = input).PrintArea.ThicknessPx);

    [Theory]
    [InlineData(-1, SettingsLimits.PrintAreaShadeMin)]
    [InlineData(5, SettingsLimits.PrintAreaShadeMin)]
    [InlineData(300, SettingsLimits.PrintAreaShadeMax)]
    [InlineData(42, 40)]
    [InlineData(43, 45)]
    public void ShadePercent_IsClampedAndRoundedToSteps(int input, int expected) =>
        Assert.Equal(expected, Normalized(s => s.PrintArea.ShadePercent = input).PrintArea.ShadePercent);

    [Fact]
    public void UndefinedEnums_BecomeDefaults_DefinedValuesStay()
    {
        var bad = Normalized(s =>
        {
            s.PrintArea.Style = (PrintAreaStyle)99;
            s.PrintArea.Color = (PrintAreaColor)99;
        }).PrintArea;
        Assert.Equal(PrintAreaStyle.LinesShaded, bad.Style);
        Assert.Equal(PrintAreaColor.White, bad.Color);

        foreach (var style in Enum.GetValues<PrintAreaStyle>())
            Assert.Equal(style, Normalized(s => s.PrintArea.Style = style).PrintArea.Style);
        foreach (var color in Enum.GetValues<PrintAreaColor>())
            Assert.Equal(color, Normalized(s => s.PrintArea.Color = color).PrintArea.Color);
    }

    [Fact]
    public void Enums_HaveTheAgreedValues_AppendOnly()
    {
        Assert.Equal(["Lines", "LinesShaded", "Corners"], Enum.GetNames<PrintAreaStyle>());
        Assert.Equal(["White", "Black", "Red", "Yellow", "Green", "Blue", "Pink", "Orange"], Enum.GetNames<PrintAreaColor>());
    }

    [Fact]
    public void MissingSection_IsReplacedByDefaults()
    {
        var s = Normalized(x => x.PrintArea = null!);

        Assert.NotNull(s.PrintArea);
        Assert.False(s.PrintArea.Enabled);
    }

    [Fact]
    public void Json_RoundTrip_KeepsAllValues_AndWritesEnumNames()
    {
        var original = new AppSettings();
        original.PrintArea.Enabled = true;
        original.PrintArea.Style = PrintAreaStyle.Corners;
        original.PrintArea.Color = PrintAreaColor.Pink;
        original.PrintArea.OpacityPercent = 55;
        original.PrintArea.ThicknessPx = 9;
        original.PrintArea.ShadePercent = 70;

        var json = JsonSerializer.Serialize(original, AppSettings.JsonOptions);
        var clone = original.Clone();

        Assert.Contains("\"Corners\"", json);
        Assert.Contains("\"Pink\"", json);
        Assert.Equal(original.PrintArea, clone.PrintArea, new PrintAreaComparer());
        Assert.NotSame(original.PrintArea, clone.PrintArea);
    }

    [Fact]
    public void Json_UnknownEnumNames_DoNotBreakTheFile_AndFallBackToDefaults()
    {
        const string json = """{ "PrintArea": { "Enabled": true, "Style": "Zukunft", "Color": "Neonlila", "OpacityPercent": 50 } }""";

        var s = SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!);

        Assert.True(s.PrintArea.Enabled);
        Assert.Equal(PrintAreaStyle.LinesShaded, s.PrintArea.Style);
        Assert.Equal(PrintAreaColor.White, s.PrintArea.Color);
        Assert.Equal(50, s.PrintArea.OpacityPercent);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var a = new PrintAreaSettings { Enabled = true, ThicknessPx = 8 };
        var b = a.Clone();
        b.ThicknessPx = 2;

        Assert.Equal(8, a.ThicknessPx);
        Assert.True(b.Enabled);
    }

    [Fact]
    public void PrintArea_IsAPortableSection_NotDeviceSpecific()
    {
        Assert.Contains(SettingsSections.PortableSections, p => p.Name == nameof(AppSettings.PrintArea));
        Assert.DoesNotContain(DeviceSpecificSettings.Paths, p => p.StartsWith("PrintArea.", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyPortableSections_CopiesPrintArea_Deeply()
    {
        var from = new AppSettings();
        from.PrintArea.Enabled = true;
        from.PrintArea.Color = PrintAreaColor.Blue;
        var to = new AppSettings();

        SettingsSections.CopyPortableSections(from, to);
        from.PrintArea.Color = PrintAreaColor.Red;

        Assert.True(to.PrintArea.Enabled);
        Assert.Equal(PrintAreaColor.Blue, to.PrintArea.Color);
    }

    [Fact]
    public void ExportImport_Roundtrip_CarriesThePrintAreaToTheOtherDevice()
    {
        var original = new AppSettings();
        original.PrintArea.Enabled = true;
        original.PrintArea.Style = PrintAreaStyle.Lines;
        original.PrintArea.Color = PrintAreaColor.Green;
        original.PrintArea.OpacityPercent = 45;
        original.PrintArea.ThicknessPx = 6;
        original.PrintArea.ShadePercent = 25;

        var json = SettingsTransfer.Serialize(original, null, "2.10.0", DateTimeOffset.Parse("2026-10-31T12:00:00+00:00"));
        var parsed = SettingsTransfer.Parse(json).Sections;

        Assert.Contains("\"PrintArea\"", json);
        Assert.Equal(original.PrintArea, parsed.PrintArea, new PrintAreaComparer());
    }

    [Fact]
    public void ImportWithoutThePrintAreaSection_GivesDefaults()
    {
        var json = SettingsTransfer.Serialize(new AppSettings(), null, "2.9.1", DateTimeOffset.Parse("2026-10-31T12:00:00+00:00"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["Settings"]!.AsObject().Remove("PrintArea");

        var parsed = SettingsTransfer.Parse(node.ToJsonString()).Sections;

        Assert.False(parsed.PrintArea.Enabled);
        Assert.Equal(PrintAreaStyle.LinesShaded, parsed.PrintArea.Style);
    }

    private sealed class PrintAreaComparer : IEqualityComparer<PrintAreaSettings>
    {
        public bool Equals(PrintAreaSettings? x, PrintAreaSettings? y) =>
            x is not null && y is not null && x.Enabled == y.Enabled && x.Style == y.Style && x.Color == y.Color
            && x.OpacityPercent == y.OpacityPercent && x.ThicknessPx == y.ThicknessPx && x.ShadePercent == y.ShadePercent;

        public int GetHashCode(PrintAreaSettings obj) => HashCode.Combine(obj.Enabled, obj.Style, obj.Color, obj.OpacityPercent, obj.ThicknessPx, obj.ShadePercent);
    }
}
