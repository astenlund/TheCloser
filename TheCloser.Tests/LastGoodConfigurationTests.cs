using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class LastGoodConfigurationTests
{
    private static IConfiguration Root(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Refresh_EmptyRootAfterNonEmpty_KeepsSnapshotAndWarnsOncePerTransition()
    {
        // Arrange
        var lastGood = new LastGoodConfiguration();
        var warnings = new List<string>();
        lastGood.Refresh(Root(("notepad", "WM_CLOSE")), warnings.Add);

        // Act
        var snapshot = lastGood.Refresh(Root(), warnings.Add);
        lastGood.Refresh(Root(), warnings.Add);
        lastGood.Refresh(Root(("notepad", "CTRL-W")), warnings.Add);
        lastGood.Refresh(Root(), warnings.Add);
        lastGood.Refresh(Root(), warnings.Add);

        // Assert
        Assert.Equal("WM_CLOSE", snapshot["notepad"]);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Refresh_UnchangedValues_ReusesSnapshot()
    {
        // Arrange
        var lastGood = new LastGoodConfiguration();
        var first = lastGood.Refresh(Root(("notepad", "WM_CLOSE")), _ => { });

        // Act
        var second = lastGood.Refresh(Root(("notepad", "WM_CLOSE")), _ => { });

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void Refresh_NeverPopulated_ReturnsEmptyWithoutWarning()
    {
        // Arrange
        var lastGood = new LastGoodConfiguration();
        var warnings = new List<string>();

        // Act
        var snapshot = lastGood.Refresh(Root(), warnings.Add);

        // Assert
        Assert.Empty(snapshot.GetChildren());
        Assert.Empty(warnings);
    }

    [Fact]
    public void Refresh_ValueCopiesBothEntryForms()
    {
        // Arrange: flat-string and nested-object forms, as the README documents.
        var lastGood = new LastGoodConfiguration();
        var live = Root(("devenv", "CTRL-F4"), ("sublime_merge:Method", "CTRL-W"), ("sublime_merge:ClickPosition", "Center"));

        // Act
        var snapshot = lastGood.Refresh(live, _ => { });
        live["devenv"] = "ALT-F4";
        live["sublime_merge:Method"] = "ALT-F2";
        live["sublime_merge:ClickPosition"] = "Left";
        var parsedFlat = ProcessSettingsParser.Parse(snapshot, "devenv", _ => { });
        var parsedNested = ProcessSettingsParser.Parse(snapshot, "sublime_merge", _ => { });

        // Assert
        Assert.Equal("CTRL-F4", parsedFlat.Method);
        Assert.Equal("CTRL-W", parsedNested.Method);
        Assert.Equal(TitleBarClickPosition.Center, parsedNested.ClickPosition);
    }
}
