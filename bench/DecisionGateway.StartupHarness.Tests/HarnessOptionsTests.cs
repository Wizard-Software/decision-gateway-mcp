using AwesomeAssertions;
using DecisionGateway.StartupHarness;

namespace DecisionGateway.StartupHarness.Tests;

[Trait("Category", "Unit")]
public sealed class HarnessOptionsTests
{
    private static readonly string Binary = Path.GetFullPath("host-binary");

    [Fact]
    public void Parse_reads_all_options()
    {
        var options = HarnessOptions.Parse(
        [
            "--binary", Binary, "--arg", "a.dll", "--arg", "--flag", "--runs", "50", "--rid", "linux-x64",
            "--output", "out/startup-linux-x64.json", "--timeout-seconds", "20",
        ]);

        options.BinaryPath.Should().Be(Binary);
        options.Arguments.Should().Equal("a.dll", "--flag");
        options.Runs.Should().Be(50);
        options.Rid.Should().Be("linux-x64");
        options.OutputPath.Should().Be("out/startup-linux-x64.json");
        options.Timeout.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Parse_applies_defaults_for_runs_and_timeout()
    {
        var options = HarnessOptions.Parse(["--binary", Binary, "--rid", "osx-arm64", "--output", "o.json"]);

        options.Runs.Should().Be(50);
        options.Timeout.Should().Be(TimeSpan.FromSeconds(10));
        options.Arguments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("--runs", "1")] // below the minimum of 2
    [InlineData("--runs", "1001")]
    [InlineData("--runs", "abc")]
    [InlineData("--rid", "linux x64")]
    [InlineData("--rid", "Linux-x64")]
    [InlineData("--rid", "linux-x64\n")] // a trailing newline must not slip past the anchors
    [InlineData("--rid", "")]
    [InlineData("--binary", "relative/path")]
    [InlineData("--timeout-seconds", "0")]
    [InlineData("--timeout-seconds", "601")]
    [InlineData("--timeout-seconds", "ten")]
    public void Parse_rejects_invalid_values(string name, string value)
    {
        var args = new List<string> { "--binary", Binary, "--runs", "50", "--rid", "linux-x64", "--output", "o.json" };
        if (name == "--timeout-seconds")
        {
            args.AddRange([name, "10"]);
        }

        var index = args.IndexOf(name);
        args[index + 1] = value;

        FluentActions.Invoking(() => HarnessOptions.Parse(args))
            .Should().Throw<ArgumentException>().WithMessage($"*{name}*");
    }

    [Theory]
    [InlineData("--binary")]
    [InlineData("--rid")]
    [InlineData("--output")]
    public void Parse_requires_mandatory_options(string name)
    {
        var args = new List<string> { "--binary", Binary, "--rid", "x", "--output", "o.json" };
        var index = args.IndexOf(name);
        args.RemoveRange(index, 2);

        FluentActions.Invoking(() => HarnessOptions.Parse(args))
            .Should().Throw<ArgumentException>().WithMessage($"*{name}*");
    }

    [Fact]
    public void Parse_rejects_an_unknown_option() =>
        FluentActions.Invoking(() => HarnessOptions.Parse(
                ["--binary", Binary, "--rid", "x", "--output", "o.json", "--bogus", "1"]))
            .Should().Throw<ArgumentException>().WithMessage("*--bogus*");

    [Fact]
    public void Parse_rejects_an_option_without_a_value() =>
        FluentActions.Invoking(() => HarnessOptions.Parse(["--binary", Binary, "--rid", "x", "--output"]))
            .Should().Throw<ArgumentException>().WithMessage("*--output*");
}
