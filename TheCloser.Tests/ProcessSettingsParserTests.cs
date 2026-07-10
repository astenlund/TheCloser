using Microsoft.Extensions.Configuration;

namespace TheCloser.Tests;

public class ProcessSettingsParserTests
{
    [Fact]
    public void Parse_SimpleStringForm_ReturnsMethodVerbatimWithoutClickPosition()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?> { ["notepad"] = "alt-f4" });

        // Act
        var settings = ProcessSettingsParser.Parse(config, "notepad");

        // Assert
        Assert.Equal("alt-f4", settings.Method);
        Assert.Null(settings.ClickPosition);
    }

    [Fact]
    public void Parse_ObjectForm_ReturnsMethodAndClickPosition()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["chrome:Method"] = "CTRL-W",
            ["chrome:ClickPosition"] = "Center"
        });

        // Act
        var settings = ProcessSettingsParser.Parse(config, "chrome");

        // Assert
        Assert.Equal("CTRL-W", settings.Method);
        Assert.Equal(TitleBarClickPosition.Center, settings.ClickPosition);
    }

    [Fact]
    public void Parse_ObjectForm_PreservesMethodCase()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?> { ["chrome:Method"] = "ctrl-w" });

        // Act
        var settings = ProcessSettingsParser.Parse(config, "chrome");

        // Assert
        Assert.Equal("ctrl-w", settings.Method);
    }

    [Fact]
    public void Parse_ClickPosition_IsCaseInsensitive()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?> { ["chrome:ClickPosition"] = "center" });

        // Act
        var settings = ProcessSettingsParser.Parse(config, "chrome");

        // Assert
        Assert.Equal(TitleBarClickPosition.Center, settings.ClickPosition);
    }

    [Fact]
    public void Parse_InvalidClickPosition_WarnsAndReturnsNull()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?> { ["chrome:ClickPosition"] = "Diagonal" });
        var warnings = new List<string>();

        // Act
        var settings = ProcessSettingsParser.Parse(config, "chrome", warnings.Add);

        // Assert
        Assert.Null(settings.ClickPosition);
        var warning = Assert.Single(warnings);
        Assert.Contains("Diagonal", warning);
    }

    [Fact]
    public void Parse_UndefinedNumericClickPosition_WarnsAndReturnsNull()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?> { ["chrome:ClickPosition"] = "5" });
        var warnings = new List<string>();

        // Act
        var settings = ProcessSettingsParser.Parse(config, "chrome", warnings.Add);

        // Assert
        Assert.Null(settings.ClickPosition);
        var warning = Assert.Single(warnings);
        Assert.Contains("5", warning);
    }

    [Fact]
    public void Parse_MissingSection_YieldsDefaults()
    {
        // Arrange
        var config = BuildConfig(new Dictionary<string, string?>());

        // Act
        var settings = ProcessSettingsParser.Parse(config, "unknown");

        // Assert
        Assert.Null(settings.Method);
        Assert.Null(settings.ClickPosition);
    }

    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
