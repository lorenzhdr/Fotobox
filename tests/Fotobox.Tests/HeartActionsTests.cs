using Fotobox.Core;
using Fotobox.Core.Heart;
using Xunit;

namespace Fotobox.Tests;

public sealed class HeartActionsTests
{
    [Fact]
    public void Catalog_CoversEveryEnumValue_ExactlyOnce()
    {
        var actions = HeartActions.All.Select(a => a.Action).ToList();
        Assert.Equal(Enum.GetValues<HeartAction>().Order(), actions.Order());
        Assert.Equal(actions.Count, actions.Distinct().Count());
    }

    [Fact]
    public void Entries_HaveGermanNameAndDescription()
    {
        foreach (var info in HeartActions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(info.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
        }
        var kisscam = Assert.Single(HeartActions.All, a => a.Action == HeartAction.Kisscam);
        Assert.Equal("Kisscam", kisscam.DisplayName);
        Assert.Contains("Jumbotron", kisscam.Description);
    }

    [Fact]
    public void Find_ReturnsEntryOrNull()
    {
        Assert.Equal(HeartAction.Kisscam, HeartActions.Find(HeartAction.Kisscam)?.Action);
        Assert.Null(HeartActions.Find((HeartAction)999));
    }
}
