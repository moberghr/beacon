using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class UpdateMcpSettingsHandlerTests
{
    private static UpdateMcpSettingsHandler CreateHandler()
    {
        // The invalid-regex guard runs before any DB access, so these doubles are never invoked on
        // the rejection path — a throwing factory proves the validation short-circuits first.
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var provider = new Mock<IMcpSettingsProvider>(MockBehavior.Strict);
        return new UpdateMcpSettingsHandler(factory.Object, provider.Object);
    }

    [TestCase("(unclosed")]
    [TestCase("a{2,1}")]
    [TestCase("*invalid")]
    public async Task Handle_InvalidCustomPiiPattern_Throws(string badPattern)
    {
        var handler = CreateHandler();
        var command = new UpdateMcpSettingsCommand(new McpSettingsData
        {
            CustomPiiPatterns = [badPattern],
        });

        var act = async () => await handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid regular expression*");
    }
}
