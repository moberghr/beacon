using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Validators;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SubscriptionValidatorTests
{
    private static QueryParameterData QueryParam(string placeholder) =>
        new()
        {
            Name = placeholder.TrimStart('@'),
            Type = ParameterType.String,
            Description = "d",
            Placeholder = placeholder
        };

    private static SubscriptionParameterData SubParam(string placeholder, string value = "value1") =>
        new()
        {
            QueryPlaceholder = placeholder,
            Value = value
        };

    [Test]
    public void ValidateParameters_DuplicatePlusMissingPlaceholder_Throws()
    {
        // Regression: query wants @a and @b, but the subscription supplies @a twice and omits @b.
        // The old counter cancelled these out and let this invalid state through.
        var queryParams = new List<QueryParameterData> { QueryParam("@a"), QueryParam("@b") };
        var subscriptionParams = new List<SubscriptionParameterData> { SubParam("@a"), SubParam("@a") };

        var act = () => SubscriptionValidator.ValidateParameters(subscriptionParams, queryParams);

        // Must fire the duplicate branch (not the count guard — counts are equal here).
        act.Should().Throw<BeaconException>().WithMessage("*multiple*");
    }

    [Test]
    public void ValidateParameters_AllPresentExactlyOnce_Passes()
    {
        var queryParams = new List<QueryParameterData> { QueryParam("@a"), QueryParam("@b") };
        var subscriptionParams = new List<SubscriptionParameterData> { SubParam("@a"), SubParam("@b") };

        var act = () => SubscriptionValidator.ValidateParameters(subscriptionParams, queryParams);

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateParameters_MissingPlaceholder_Throws()
    {
        // Same count, but @b is missing and an unrelated @c is present.
        var queryParams = new List<QueryParameterData> { QueryParam("@a"), QueryParam("@b") };
        var subscriptionParams = new List<SubscriptionParameterData> { SubParam("@a"), SubParam("@c") };

        var act = () => SubscriptionValidator.ValidateParameters(subscriptionParams, queryParams);

        act.Should().Throw<BeaconException>().WithMessage("*Not all*");
    }

    [Test]
    public void ValidateParameters_CountMismatch_Throws()
    {
        var queryParams = new List<QueryParameterData> { QueryParam("@a"), QueryParam("@b") };
        var subscriptionParams = new List<SubscriptionParameterData> { SubParam("@a") };

        var act = () => SubscriptionValidator.ValidateParameters(subscriptionParams, queryParams);

        act.Should().Throw<BeaconException>().WithMessage("*does not match*");
    }

    [Test]
    public void ValidateParameters_NoQueryParams_ClearsSubscriptionParamsAndPasses()
    {
        var queryParams = new List<QueryParameterData>();
        var subscriptionParams = new List<SubscriptionParameterData> { SubParam("@a") };

        var act = () => SubscriptionValidator.ValidateParameters(subscriptionParams, queryParams);

        act.Should().NotThrow();
        subscriptionParams.Should().BeEmpty();
    }
}
