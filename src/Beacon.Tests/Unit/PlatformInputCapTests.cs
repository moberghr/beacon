using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Handlers.DataQuality.GetEvaluationHistory;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

[TestFixture]
public class PlatformInputCapTests
{
    [TestCase(int.MaxValue, InputLimits.MaxListTake)]
    [TestCase(-5, 1)]
    [TestCase(0, 1)]
    [TestCase(50, 50)]
    public async Task EvaluationHistory_ClampsTake(int take, int expected)
    {
        var service = new Mock<IDataQualityEvaluationService>();
        service
            .Setup(x => x.GetEvaluationHistoryAsync(7, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await new GetEvaluationHistoryHandler(service.Object)
            .Handle(new GetEvaluationHistoryQuery(7, take), CancellationToken.None);

        service.Verify(x => x.GetEvaluationHistoryAsync(7, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GoldenCaseEdit_WithOversizedFields_IsRefused()
    {
        var handler = new UpdateGoldenCaseHandler(
            null!,
            Mock.Of<IGroundingSqlCheck>(),
            null!,
            null!,
            NullLogger<UpdateGoldenCaseHandler>.Instance);

        var question = () => handler.Handle(new UpdateGoldenCaseCommand { Id = 1, Question = new string('q', InputLimits.GoldenQuestionMaxLength + 1) }, CancellationToken.None);
        var sql = () => handler.Handle(new UpdateGoldenCaseCommand { Id = 1, GoldSql = new string('s', InputLimits.GoldenSqlMaxLength + 1) }, CancellationToken.None);
        var notes = () => handler.Handle(new UpdateGoldenCaseCommand { Id = 1, Notes = new string('n', InputLimits.GoldenNotesMaxLength + 1) }, CancellationToken.None);

        await question.Should().ThrowAsync<BeaconValidationException>();
        await sql.Should().ThrowAsync<BeaconValidationException>();
        await notes.Should().ThrowAsync<BeaconValidationException>();
    }

    [Test]
    public async Task GoldenPromotion_WithOversizedNotes_IsRefused()
    {
        var handler = new PromoteSignalToGoldenHandler(
            null!,
            Mock.Of<IGroundingSqlCheck>(),
            null!,
            null!,
            NullLogger<PromoteSignalToGoldenHandler>.Instance);

        var act = () => handler.Handle(new PromoteSignalToGoldenCommand(1, "hash", new string('n', InputLimits.GoldenNotesMaxLength + 1)), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconValidationException>();
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(59)]
    [TestCase(80)]
    public void ByteBudget_NeverReturnsOverBudget(int budget)
    {
        var result = ToolHelper.WithinByteBudget(new string('a', 5000), budget);

        JsonSerializer.Serialize(result).Length.Should().BeLessThanOrEqualTo(budget + 2);
        System.Text.Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(budget);
    }

    [Test]
    public void ByteBudget_CountsTheEscapedSize()
    {
        var text = new string('\n', 1000) + new string('"', 1000);

        var result = ToolHelper.WithinByteBudget(text, 500);

        var wire = JsonSerializer.Serialize(result);
        (System.Text.Encoding.UTF8.GetByteCount(wire) - 2).Should().BeLessThanOrEqualTo(500);
    }
}
