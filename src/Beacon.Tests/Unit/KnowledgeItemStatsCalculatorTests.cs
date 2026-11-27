using Beacon.AI.Services.Learning;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC2: the weights of <see cref="KnowledgeItemStatsCalculator"/>. A curator verdict weighs 3, any other verdict 1, an
/// unrated successful answer adds 0.25 helpful and a failed answer or one re-run with edited SQL 0.25 harmful; a verdict
/// wins over the implicit outcome; per item and score one caller adds at most 2 from its verdicts and implicit outcomes and
/// at most 6 from curator verdicts. The caller is the caller hash, else the user, else one shared anonymous caller.
/// </summary>
[TestFixture]
public class KnowledgeItemStatsCalculatorTests
{
    private static readonly KnowledgeItemKey Golden = new(McpKnowledgeItemKind.GoldenCase, 7);
    private static readonly KnowledgeItemKey Pattern = new(McpKnowledgeItemKind.LearnedPattern, 7);
    private static readonly KnowledgeItemKey Term = new(McpKnowledgeItemKind.GlossaryTerm, 3);

    private int _nextSignalId;

    [SetUp]
    public void SetUp()
    {
        _nextSignalId = 100;
    }

    [TestCase(McpUserVerdict.Correct, true, 3, 0)]
    [TestCase(McpUserVerdict.Correct, false, 1, 0)]
    [TestCase(McpUserVerdict.Incorrect, true, 0, 3)]
    [TestCase(McpUserVerdict.Incorrect, false, 0, 1)]
    public void Compute_AVerdict_WeighsByWhoRecordedIt(McpUserVerdict verdict, bool fromCurator, double helpful, double harmful)
    {
        var scores = KnowledgeItemStatsCalculator.Compute([Signal(verdict: verdict, fromCurator: fromCurator)]);

        var score = scores.Should().ContainSingle().Subject;
        score.Item.Should().Be(Golden);
        score.HelpfulScore.Should().Be(helpful);
        score.HarmfulScore.Should().Be(harmful);
        score.SignalCount.Should().Be(1);
        score.CallerCount.Should().Be(1);
    }

    [TestCase(true, false, 0.25, 0)]
    [TestCase(false, false, 0, 0.25)]
    [TestCase(true, true, 0, 0.25)]
    [TestCase(false, true, 0, 0.25)]
    public void Compute_WithoutAVerdict_TheOutcomeAddsAQuarter(bool succeeded, bool editedRerun, double helpful, double harmful)
    {
        var scores = KnowledgeItemStatsCalculator.Compute([Signal(succeeded: succeeded, editedRerun: editedRerun)]);

        var score = scores.Should().ContainSingle().Subject;
        score.HelpfulScore.Should().Be(helpful);
        score.HarmfulScore.Should().Be(harmful, "a failed answer that was also re-run edited adds one quarter, not two");
    }

    [TestCase(McpUserVerdict.Correct, false, true, 1, 0)]
    [TestCase(McpUserVerdict.Correct, true, false, 1, 0)]
    [TestCase(McpUserVerdict.Incorrect, true, false, 0, 1)]
    public void Compute_AVerdict_WinsOverTheImplicitOutcome(
        McpUserVerdict verdict, bool succeeded, bool editedRerun, double helpful, double harmful)
    {
        var scores = KnowledgeItemStatsCalculator.Compute(
            [Signal(verdict: verdict, succeeded: succeeded, editedRerun: editedRerun)]);

        var score = scores.Should().ContainSingle().Subject;
        score.HelpfulScore.Should().Be(helpful);
        score.HarmfulScore.Should().Be(harmful);
    }

    [Test]
    public void Compute_OneCallersVerdicts_AddAtMostTwoPerScore()
    {
        var signals = Enumerable.Range(0, 3)
            .Select(_ => Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a"))
            .Concat(Enumerable.Range(0, 3).Select(_ => Signal(verdict: McpUserVerdict.Incorrect, callerHash: "caller-a")))
            .ToList();

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.HelpfulScore.Should().Be(2);
        score.HarmfulScore.Should().Be(2);
        score.SignalCount.Should().Be(6, "every signal that carried the item counts, capped or not");
        score.CallerCount.Should().Be(1);
    }

    [Test]
    public void Compute_OneCallersImplicitOutcomes_AddAtMostTwo()
    {
        var signals = Enumerable.Range(0, 12)
            .Select(_ => Signal(userId: 5))
            .ToList();

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.HelpfulScore.Should().Be(2, "twelve quarters are three, capped at two");
        score.HarmfulScore.Should().Be(0);
        score.SignalCount.Should().Be(12);
    }

    [Test]
    public void Compute_CuratorVerdictsOnOneCallersSignals_AddAtMostSix()
    {
        var signals = Enumerable.Range(0, 3)
            .Select(_ => Signal(verdict: McpUserVerdict.Incorrect, fromCurator: true, callerHash: "caller-a"))
            .ToList();

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.HarmfulScore.Should().Be(6, "three curator verdicts are nine, capped at six");
        score.HelpfulScore.Should().Be(0);
    }

    [Test]
    public void Compute_CuratorVerdicts_AreCappedApartFromTheCallersOwn()
    {
        // Three own Correct verdicts (3, capped at 2) and one curator Correct verdict (3) on the same caller's signals.
        var signals = Enumerable.Range(0, 3)
            .Select(_ => Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a"))
            .Append(Signal(verdict: McpUserVerdict.Correct, fromCurator: true, callerHash: "caller-a"))
            .ToList();

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.HelpfulScore.Should().Be(5);
        score.CallerCount.Should().Be(1);
    }

    [Test]
    public void Compute_TheCap_IsPerCaller()
    {
        var signals = new[] { "caller-a", "caller-b" }
            .SelectMany(x => Enumerable.Range(0, 3).Select(_ => Signal(verdict: McpUserVerdict.Correct, callerHash: x)))
            .ToList();

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.HelpfulScore.Should().Be(4);
        score.SignalCount.Should().Be(6);
        score.CallerCount.Should().Be(2);
    }

    [Test]
    public void Compute_TheCap_IsPerItem()
    {
        var signals = Enumerable.Range(0, 3)
            .Select(_ => Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a", items: [Golden, Term]))
            .ToList();

        var scores = KnowledgeItemStatsCalculator.Compute(signals);

        scores.Should().HaveCount(2);
        scores.Should().OnlyContain(x => x.HelpfulScore == 2);
        scores.Should().OnlyContain(x => x.SignalCount == 3);
    }

    [Test]
    public void Compute_TheCallerHash_WinsOverTheUser()
    {
        // The same hash under two users is one caller; the same user without a hash is another one.
        var signals = new[]
        {
            Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a", userId: 1),
            Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a", userId: 2),
            Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a", userId: 3),
            Signal(verdict: McpUserVerdict.Correct, userId: 1)
        };

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.CallerCount.Should().Be(2);
        score.HelpfulScore.Should().Be(3, "the hash's three verdicts cap at two, the user adds one");
    }

    [Test]
    public void Compute_AnEmptyCallerHash_FallsBackToTheUser()
    {
        var signals = new[]
        {
            Signal(verdict: McpUserVerdict.Correct, callerHash: "", userId: 9),
            Signal(verdict: McpUserVerdict.Correct, userId: 9)
        };

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.CallerCount.Should().Be(1);
    }

    [Test]
    public void Compute_AHashAndAUserWithTheSameText_AreTwoCallers()
    {
        var signals = new[]
        {
            Signal(verdict: McpUserVerdict.Correct, callerHash: "5"),
            Signal(verdict: McpUserVerdict.Correct, userId: 5)
        };

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.CallerCount.Should().Be(2);
    }

    [Test]
    public void Compute_SignalsWithoutCallerHashOrUser_AreOneAnonymousCaller()
    {
        var signals = Enumerable.Range(0, 3)
            .Select(_ => Signal(verdict: McpUserVerdict.Incorrect))
            .ToList();

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.CallerCount.Should().Be(1);
        score.HarmfulScore.Should().Be(2, "the anonymous caller is capped like any other");
        score.SignalCount.Should().Be(3);
    }

    [Test]
    public void Compute_ASignalThatCarriedAnItemTwice_CountsOnce()
    {
        var scores = KnowledgeItemStatsCalculator.Compute(
            [Signal(verdict: McpUserVerdict.Correct, items: [Golden, Golden])]);

        var score = scores.Should().ContainSingle().Subject;
        score.SignalCount.Should().Be(1);
        score.HelpfulScore.Should().Be(1);
    }

    [Test]
    public void Compute_TheSameIdOfAnotherKind_IsAnotherItem_AndTheScoresAreOrderedByKindAndId()
    {
        var scores = KnowledgeItemStatsCalculator.Compute(
        [
            Signal(verdict: McpUserVerdict.Correct, items: [Term, Pattern]),
            Signal(verdict: McpUserVerdict.Incorrect, callerHash: "caller-b", items: [Golden])
        ]);

        scores.Select(x => x.Item).Should().Equal(Golden, Pattern, Term);
        scores[0].HarmfulScore.Should().Be(1);
        scores[0].HelpfulScore.Should().Be(0);
        scores[1].HelpfulScore.Should().Be(1);
        scores[2].HelpfulScore.Should().Be(1);
    }

    [Test]
    public void Compute_NoSignals_GivesNoScores()
    {
        KnowledgeItemStatsCalculator.Compute([]).Should().BeEmpty();
    }

    [Test]
    public void Compute_AMixOfCallersAndOutcomes_SumsEachCallersCappedShare()
    {
        var signals = new[]
        {
            // caller-a: own Correct (1) + unrated success (0.25) helpful; curator Incorrect (3) harmful.
            Signal(verdict: McpUserVerdict.Correct, callerHash: "caller-a"),
            Signal(callerHash: "caller-a"),
            Signal(verdict: McpUserVerdict.Incorrect, fromCurator: true, callerHash: "caller-a"),

            // user 4: failed (0.25) and edited re-run (0.25) harmful.
            Signal(succeeded: false, userId: 4),
            Signal(editedRerun: true, userId: 4),

            // anonymous: unrated success (0.25) helpful.
            Signal()
        };

        var score = KnowledgeItemStatsCalculator.Compute(signals).Should().ContainSingle().Subject;

        score.HelpfulScore.Should().Be(1.5);
        score.HarmfulScore.Should().Be(3.5);
        score.SignalCount.Should().Be(6);
        score.CallerCount.Should().Be(3);
    }

    private KnowledgeItemSignal Signal(
        McpUserVerdict verdict = McpUserVerdict.Unset,
        bool fromCurator = false,
        bool succeeded = true,
        bool editedRerun = false,
        string? callerHash = null,
        int? userId = null,
        KnowledgeItemKey[]? items = null)
    {
        return new KnowledgeItemSignal
        {
            SignalId = _nextSignalId++,
            CallerHash = callerHash,
            UserId = userId,
            Verdict = verdict,
            FromCurator = fromCurator,
            Succeeded = succeeded,
            HasEditedRerun = editedRerun,
            Items = items ?? [Golden]
        };
    }
}
