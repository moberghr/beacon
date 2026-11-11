using Beacon.Doctor;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// Where <c>beacon-doctor</c> takes its access token from: standard input with <c>--token-stdin</c>, the
/// <c>BEACON_MCP_TOKEN</c> environment variable, or (still accepted, with a warning) <c>--token</c>.
/// </summary>
[TestFixture]
public class BeaconDoctorTokenTests
{
    private const string Url = "https://beacon.example.com/beacon/mcp";

    [Test]
    public void WithTokenStdin_TheTokenIsTheFirstLineOfStandardInput_WithoutAWarning()
    {
        var warnings = new StringWriter();

        var token = DoctorProgram.ReadToken(["mcp", Url, "--token-stdin"], "from-environment", new StringReader("  eyJ.from.stdin \nsecond line\n"), warnings);

        token.Should().Be("eyJ.from.stdin");
        warnings.ToString().Should().BeEmpty();
    }

    [Test]
    public void WithTokenStdin_AndNothingOnStandardInput_ThereIsNoToken()
    {
        var token = DoctorProgram.ReadToken(["mcp", Url, "--token-stdin"], "from-environment", new StringReader(string.Empty), new StringWriter());

        token.Should().BeNull();
    }

    [Test]
    public void WithoutAnOption_TheTokenComesFromTheEnvironment_WithoutAWarning()
    {
        var warnings = new StringWriter();

        var token = DoctorProgram.ReadToken(["mcp", Url, "--json"], "from-environment", new StringReader("ignored"), warnings);

        token.Should().Be("from-environment");
        warnings.ToString().Should().BeEmpty();
    }

    [Test]
    public void WithoutAnyToken_ThereIsNone()
    {
        DoctorProgram.ReadToken(["mcp", Url], "  ", new StringReader("ignored"), new StringWriter()).Should().BeNull();
        DoctorProgram.ReadToken(["mcp", Url], null, new StringReader("ignored"), new StringWriter()).Should().BeNull();
    }

    [Test]
    public void TheTokenOption_StillWorks_AndWarnsOnOneLineWithoutTheToken()
    {
        var warnings = new StringWriter();

        var token = DoctorProgram.ReadToken(["mcp", Url, "--token", "eyJ.from.argument", "--json"], "from-environment", new StringReader("ignored"), warnings);

        token.Should().Be("eyJ.from.argument");
        var lines = warnings.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle()
            .Which.Should().Contain("--token").And.Contain(DoctorProgram.TokenVariable).And.Contain("--token-stdin")
            .And.NotContain("eyJ.from.argument");
    }

    [Test]
    public void WithBothOptions_StandardInputWins_AndTheTokenOptionIsStillWarnedAbout()
    {
        var warnings = new StringWriter();

        var token = DoctorProgram.ReadToken(["mcp", Url, "--token", "eyJ.from.argument", "--token-stdin"], null, new StringReader("eyJ.from.stdin"), warnings);

        token.Should().Be("eyJ.from.stdin");
        warnings.ToString().Should().Contain("warning: --token");
    }
}
