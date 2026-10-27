using System.Windows.Input;
using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

public class HintTextFormatterTests
{
    private static KeyChord K(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);

    private static readonly KeyChord Space = K(Key.Space);
    private static readonly KeyChord Enter = K(Key.Enter);
    private static readonly KeyChord F5 = K(Key.F5);
    private static readonly KeyChord F6 = K(Key.F6);

    // ---- Automatischer Text

    [Theory]
    [InlineData(false, "Drücke die Leertaste für ein Foto 📸")]
    [InlineData(true, "Drücke die Leertaste oder klicke für ein Foto 📸")]
    public void BuildAutomatic_SingleSpace_UsesArticle(bool click, string expected) =>
        Assert.Equal(expected, HintTextFormatter.BuildAutomatic([Space], click));

    [Fact]
    public void BuildAutomatic_SingleOtherKey_HasNoArticle()
    {
        Assert.Equal("Drücke Eingabetaste für ein Foto 📸", HintTextFormatter.BuildAutomatic([Enter], false));
        Assert.Equal("Drücke Strg+F5 für ein Foto 📸", HintTextFormatter.BuildAutomatic([K(Key.F5, ModifierKeys.Control)], false));
    }

    [Fact]
    public void BuildAutomatic_MultipleKeys_ListsAtMostThree_AndMentionsClickInDashes()
    {
        Assert.Equal("Drücke Leertaste oder Eingabetaste für ein Foto 📸", HintTextFormatter.BuildAutomatic([Space, Enter], false));
        Assert.Equal("Drücke Leertaste, Eingabetaste oder F5 für ein Foto 📸", HintTextFormatter.BuildAutomatic([Space, Enter, F5, F6], false));
        Assert.Equal("Drücke Leertaste oder Eingabetaste – oder klicke – für ein Foto 📸", HintTextFormatter.BuildAutomatic([Space, Enter], true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_WithoutTemplate_IsAutomatic(string? template)
    {
        Assert.Equal(HintTextFormatter.BuildAutomatic([Space, Enter], true), HintTextFormatter.Format(template, [Space, Enter], true));
        Assert.Equal(HintTextFormatter.BuildAutomatic([Space], false), HintTextFormatter.Format(template, [Space], false));
    }

    // ---- Platzhalter

    [Fact]
    public void Format_IndividualKeys_UseDisplayNameWithoutArticle()
    {
        var text = HintTextFormatter.Format("Taste 1: {Fototaste_1}, Taste 2: {Fototaste_2}", [Space, Enter], false);

        Assert.Equal("Taste 1: Leertaste, Taste 2: Eingabetaste", text);
    }

    [Fact]
    public void Format_PlaceholdersAreCaseInsensitive()
    {
        var text = HintTextFormatter.Format("{FOTOTASTE_1} {fototasten} {kLiCk}", [Space, Enter], true);

        Assert.Equal("Leertaste Leertaste oder Eingabetaste oder klicke", text);
    }

    [Fact]
    public void Format_AllKeys_AreJoinedWithOder_WithoutLimit()
    {
        Assert.Equal("Taste: Leertaste", HintTextFormatter.Format("Taste: {Fototasten}", [Space], false));
        Assert.Equal("Taste: Leertaste oder Eingabetaste", HintTextFormatter.Format("Taste: {Fototasten}", [Space, Enter], false));
        Assert.Equal("Taste: Leertaste, Eingabetaste, F5 oder F6",
            HintTextFormatter.Format("Taste: {Fototasten}", [Space, Enter, F5, F6], false));
    }

    [Fact]
    public void Format_Click_OnlyWhenMouseTriggers()
    {
        Assert.Equal("Drücke Leertaste oder klicke!", HintTextFormatter.Format("Drücke {Fototaste_1} {Klick}!", [Space], true));
        Assert.Equal("Drücke Leertaste!", HintTextFormatter.Format("Drücke {Fototaste_1} {Klick}!", [Space], false));
    }

    [Fact]
    public void Format_UnassignedKeyNumber_BecomesEmpty()
    {
        Assert.Equal("Drücke Leertaste", HintTextFormatter.Format("Drücke {Fototaste_1} {Fototaste_2}", [Space], false));
        Assert.Equal("Drücke Leertaste", HintTextFormatter.Format("Drücke {Fototaste_1} {Fototaste_99999999999}", [Space], false));
    }

    [Fact]
    public void Format_UnknownPlaceholders_StayLiteral()
    {
        Assert.Equal("Hallo {Foo} und {Fototaste_0} und {} Leertaste",
            HintTextFormatter.Format("Hallo {Foo} und {Fototaste_0} und {} {Fototaste_1}", [Space], false));
        Assert.Equal("Offen { Leertaste", HintTextFormatter.Format("Offen { {Fototaste_1}", [Space], false));
    }

    [Fact]
    public void Format_CollapsesSpacesAndTrims()
    {
        Assert.Equal("Drücke Leertaste für ein Foto",
            HintTextFormatter.Format("  Drücke   {Fototaste_1}   {Fototaste_2}  für ein   Foto  ", [Space], false));
    }

    [Fact]
    public void Format_RemovesSpaceBeforePunctuation_ThatEmptyPlaceholdersLeave()
    {
        Assert.Equal("Leertaste, F5.", HintTextFormatter.Format("{Fototaste_1} , {Fototaste_2} .", [Space, F5], false));
        Assert.Equal("Drücke Leertaste!", HintTextFormatter.Format("Drücke {Fototaste_1} {Klick} !", [Space], false));
        Assert.Equal("Wirklich?", HintTextFormatter.Format("Wirklich {Fototaste_3} ?", [Space], false));
        Assert.Equal("Foto, bitte.", HintTextFormatter.Format("Foto {Klick} , bitte .", [Space], false));
    }

    [Fact]
    public void Format_DoesNotTouchKeyNamesThatContainSpacesOrPunctuation()
    {
        // „Ziffernblock ,“ enthält ein Leerzeichen vor einem Komma – das ist Teil des Tastennamens.
        var text = HintTextFormatter.Format("Drücke {Fototaste_1}!", [K(Key.Decimal)], false);

        Assert.Equal("Drücke Ziffernblock ,!", text);
    }

    [Fact]
    public void Format_NothingLeft_FallsBackToAutomatic()
    {
        Assert.Equal(HintTextFormatter.BuildAutomatic([Space], false), HintTextFormatter.Format("{Fototaste_5}", [Space], false));
        Assert.Equal(HintTextFormatter.BuildAutomatic([Space], false), HintTextFormatter.Format("{Klick}", [Space], false));
    }

    [Fact]
    public void Format_PlainTextWithoutPlaceholders_IsKept()
    {
        Assert.Equal("Willkommen bei uns 🎉", HintTextFormatter.Format("Willkommen bei uns 🎉", [Space], true));
    }

    // ---- Hinweise für den Admin

    [Fact]
    public void FindProblems_ValidTemplate_HasNone()
    {
        Assert.Empty(HintTextFormatter.FindProblems("Drücke {Fototaste_1} oder {Fototaste_2} {Klick} {Fototasten}", 2));
        Assert.Empty(HintTextFormatter.FindProblems(null, 1));
        Assert.Empty(HintTextFormatter.FindProblems("   ", 1));
        Assert.Empty(HintTextFormatter.FindProblems("Nur Text", 1));
    }

    [Fact]
    public void FindProblems_UnassignedKey_NamesPlaceholderAndCount()
    {
        var problems = HintTextFormatter.FindProblems("Drücke {Fototaste_3}", 2);

        Assert.Equal(["{Fototaste_3} ist nicht belegt (es sind 2 Fototasten belegt) und bleibt leer."], problems);
    }

    [Theory]
    [InlineData(1, "es ist 1 Fototaste belegt")]
    [InlineData(0, "es ist keine Fototaste belegt")]
    [InlineData(5, "es sind 5 Fototasten belegt")]
    public void FindProblems_CountWording(int keyCount, string expected)
    {
        var problems = HintTextFormatter.FindProblems("{Fototaste_9}", keyCount);

        Assert.Equal([$"{{Fototaste_9}} ist nicht belegt ({expected}) und bleibt leer."], problems);
    }

    [Fact]
    public void FindProblems_UnknownPlaceholder_IsReportedAsWritten()
    {
        var problems = HintTextFormatter.FindProblems("{Foo} {fototaste_0} {Klick}", 1);

        Assert.Equal(
        [
            "Unbekannter Platzhalter {Foo} wird unverändert angezeigt.",
            "Unbekannter Platzhalter {fototaste_0} wird unverändert angezeigt.",
        ], problems);
    }

    [Fact]
    public void FindProblems_ReportsEachPlaceholderOnlyOnce_CaseInsensitive()
    {
        var problems = HintTextFormatter.FindProblems("{Foo} {FOO} {Fototaste_4} {fototaste_4}", 1);

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void BuildAutomatic_NoKeys_HasNoDoubleSpaces()
    {
        Assert.Equal("Klicke für ein Foto 📸", HintTextFormatter.BuildAutomatic([], true));
        Assert.Equal("Mach ein Foto 📸", HintTextFormatter.BuildAutomatic([], false));
        Assert.DoesNotContain("  ", HintTextFormatter.BuildAutomatic([], true));
        Assert.DoesNotContain("  ", HintTextFormatter.BuildAutomatic([], false));
    }

    [Fact]
    public void Format_EmptyTemplateAndNoKeys_FallsBackToTextWithoutDoubleSpaces() =>
        Assert.Equal("Klicke für ein Foto 📸", HintTextFormatter.Format(null, [], true));

    // ---- Video-Hinweiszeile

    [Fact]
    public void BuildAutomaticVideo_SingleKey_UsesArticleForEnterAndSpaceOnly()
    {
        Assert.Equal("Drücke die Eingabetaste für ein Video 🎬", HintTextFormatter.BuildAutomaticVideo([Enter]));
        Assert.Equal("Drücke die Leertaste für ein Video 🎬", HintTextFormatter.BuildAutomaticVideo([Space]));
        Assert.Equal("Drücke F5 für ein Video 🎬", HintTextFormatter.BuildAutomaticVideo([F5]));
        Assert.Equal("Drücke Strg+F5 für ein Video 🎬", HintTextFormatter.BuildAutomaticVideo([K(Key.F5, ModifierKeys.Control)]));
    }

    [Fact]
    public void BuildAutomaticVideo_MultipleKeys_ListsAtMostThree()
    {
        Assert.Equal("Drücke Eingabetaste oder F5 für ein Video 🎬", HintTextFormatter.BuildAutomaticVideo([Enter, F5]));
        Assert.Equal("Drücke Eingabetaste, F5 oder F6 für ein Video 🎬", HintTextFormatter.BuildAutomaticVideo([Enter, F5, F6, Space]));
    }

    [Fact]
    public void BuildAutomaticVideo_NoKeys_IsEmpty()
    {
        Assert.Equal("", HintTextFormatter.BuildAutomaticVideo([]));
        Assert.Equal("", HintTextFormatter.FormatVideo(null, []));
        Assert.Equal("", HintTextFormatter.FormatVideo("   ", [], true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FormatVideo_WithoutTemplate_IsAutomatic(string? template) =>
        Assert.Equal(HintTextFormatter.BuildAutomaticVideo([Enter, F5]), HintTextFormatter.FormatVideo(template, [Enter, F5]));

    [Fact]
    public void FormatVideo_Placeholders_UseVideoKeys_CaseInsensitive()
    {
        Assert.Equal("Video mit Eingabetaste oder F5 – Taste 2: F5",
            HintTextFormatter.FormatVideo("Video mit {Videotasten} – Taste 2: {VIDEOTASTE_2}", [Enter, F5]));
        Assert.Equal("Eingabetaste", HintTextFormatter.FormatVideo("{videotaste_1}", [Enter, F5]));
    }

    [Fact]
    public void FormatVideo_UnassignedNumber_BecomesEmpty_WithoutDoubleSpaces()
    {
        Assert.Equal("Video mit Eingabetaste!", HintTextFormatter.FormatVideo("Video mit {Videotaste_1} {Videotaste_2} !", [Enter]));
        Assert.Equal("Drücke Eingabetaste, F5!", HintTextFormatter.FormatVideo("Drücke {Videotaste_1}, {Videotaste_2} {Videotaste_3}!", [Enter, F5]));
    }

    [Fact]
    public void FormatVideo_OnlyUnassignedPlaceholder_FallsBackToAutomatic()
    {
        Assert.Equal("Drücke die Eingabetaste für ein Video 🎬", HintTextFormatter.FormatVideo("{Videotaste_5}", [Enter]));
        Assert.Equal("", HintTextFormatter.FormatVideo("{Videotaste_1}", []));
    }

    [Fact]
    public void FormatVideo_UnknownPlaceholder_StaysLiteral()
    {
        Assert.Equal("Los {Foo} Eingabetaste", HintTextFormatter.FormatVideo("Los {Foo} {Videotaste_1}", [Enter]));
    }

    [Fact]
    public void FormatVideo_PhotoPlaceholders_AndClick_WorkInVideoLine()
    {
        Assert.Equal("Foto: Leertaste, Video: Eingabetaste",
            HintTextFormatter.FormatVideo("Foto: {Fototaste_1}, Video: {Videotaste_1}", [Enter], false, [Space]));
        Assert.Equal("Foto: Leertaste oder F5 | Eingabetaste",
            HintTextFormatter.FormatVideo("Foto: {Fototasten} | {Videotasten}", [Enter], false, [Space, F5]));
        // Ohne Fototasten: leer.
        Assert.Equal("Video Eingabetaste", HintTextFormatter.FormatVideo("Video {Fototaste_1} {Videotaste_1}", [Enter]));
        Assert.Equal("Eingabetaste oder klicke", HintTextFormatter.FormatVideo("{Videotaste_1} {Klick}", [Enter], true));
        Assert.Equal("Eingabetaste", HintTextFormatter.FormatVideo("{Videotaste_1} {Klick}", [Enter], false));
    }

    [Fact]
    public void Format_VideoPlaceholders_WorkInPhotoLine_AndAreEmptyWithoutVideoKeys()
    {
        Assert.Equal("Foto Leertaste, Video Eingabetaste",
            HintTextFormatter.Format("Foto {Fototaste_1}, Video {Videotaste_1}", [Space], false, [Enter]));
        Assert.Equal("Leertaste | Eingabetaste oder F5",
            HintTextFormatter.Format("{Fototasten} | {Videotasten}", [Space], false, [Enter, F5]));
        Assert.Equal("Foto Leertaste", HintTextFormatter.Format("Foto {Fototaste_1} {Videotaste_1}", [Space], false));
    }

    [Fact]
    public void FindVideoProblems_ValidTemplate_HasNone()
    {
        Assert.Empty(HintTextFormatter.FindVideoProblems("{Videotaste_1} {Videotaste_2} {Videotasten} {Klick}", 2));
        Assert.Empty(HintTextFormatter.FindVideoProblems(null, 0));
        Assert.Empty(HintTextFormatter.FindVideoProblems("Foto {Fototaste_1}", 1, 1));
    }

    [Fact]
    public void FindVideoProblems_UnassignedNumber_NamesPlaceholderAndCount()
    {
        Assert.Equal(["{Videotaste_3} ist nicht belegt (es sind 2 Videotasten belegt) und bleibt leer."],
            HintTextFormatter.FindVideoProblems("Drücke {Videotaste_3}", 2));
    }

    [Theory]
    [InlineData(1, "es ist 1 Videotaste belegt")]
    [InlineData(0, "es ist keine Videotaste belegt")]
    [InlineData(5, "es sind 5 Videotasten belegt")]
    public void FindVideoProblems_CountWording(int keyCount, string expected) =>
        Assert.Equal([$"{{Videotaste_9}} ist nicht belegt ({expected}) und bleibt leer."],
            HintTextFormatter.FindVideoProblems("{Videotaste_9}", keyCount));

    [Fact]
    public void FindVideoProblems_PhotoPlaceholderInVideoLine_UsesPhotoKeyCount()
    {
        Assert.Equal(["{Fototaste_2} ist nicht belegt (es ist 1 Fototaste belegt) und bleibt leer."],
            HintTextFormatter.FindVideoProblems("{Fototaste_2}", 3, 1));
    }

    [Fact]
    public void FindProblems_VideoPlaceholderInPhotoLine_UsesVideoKeyCount()
    {
        Assert.Empty(HintTextFormatter.FindProblems("{Videotaste_1}", 1, 1));
        Assert.Equal(["{Videotaste_2} ist nicht belegt (es ist 1 Videotaste belegt) und bleibt leer."],
            HintTextFormatter.FindProblems("{Videotaste_2}", 1, 1));
    }

    [Fact]
    public void FindVideoProblems_UnknownPlaceholder_AndZeroNumber_AreReported()
    {
        Assert.Equal(
        [
            "Unbekannter Platzhalter {Videotaste_0} wird unverändert angezeigt.",
            "Unbekannter Platzhalter {Videotaster} wird unverändert angezeigt.",
        ], HintTextFormatter.FindVideoProblems("{Videotaste_0} {Videotaster}", 1));
    }
}
