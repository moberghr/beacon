using System.Net;
using System.Text.Json;
using Beacon.AI.Services.LlmProviders;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// OpenAI's reasoning models (o-series, GPT-5 and later) refuse a request that sets a temperature (HTTP 400). The OpenAI
/// provider sends the request's temperature only to a model known to take it (GPT-3.5 and GPT-4 families) and leaves it
/// out for every other model id. An Azure deployment has a free name: one that names a reasoning model is sent what such
/// a model takes (<c>max_completion_tokens</c>, no temperature), any other gets the request as before.
/// </summary>
[TestFixture]
public class OpenAiSamplingTests
{
    private const string Completion = """{"id":"x","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"SELECT 1"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""";

    [TestCase("gpt-4o")]
    [TestCase("gpt-4o-mini")]
    [TestCase("gpt-4.1")]
    [TestCase("gpt-4-turbo")]
    [TestCase("gpt-3.5-turbo")]
    [TestCase("GPT-4o")]
    [TestCase("ft:gpt-4o-mini:acme::abc123")]
    public void AcceptsTemperature_ForTheGpt4AndGpt35Families(string model)
    {
        OpenAiSampling.AcceptsTemperature(model).Should().BeTrue();
    }

    [TestCase("gpt-6.2-astra")]
    [TestCase("gpt-6.2-sol")]
    [TestCase("gpt-6.1-terra")]
    [TestCase("gpt-6-luna")]
    [TestCase("gpt-5")]
    [TestCase("gpt-5-mini")]
    [TestCase("o1")]
    [TestCase("o3-mini")]
    [TestCase("ft:gpt-6-luna:acme::abc123")]
    [TestCase("some-future-model")]
    [TestCase("")]
    [TestCase(null)]
    public void RefusesTemperature_ForReasoningAndUnrecognisedModels(string? model)
    {
        OpenAiSampling.AcceptsTemperature(model).Should().BeFalse();
    }

    [Test]
    public async Task OpenAi_SendsTheTemperature_ToAModelThatTakesIt()
    {
        var body = await OpenAiRequestBody("gpt-4o-mini");

        body.TryGetProperty("temperature", out _).Should().BeTrue();
    }

    [TestCase("gpt-6.2-sol")]
    [TestCase("gpt-6-luna")]
    [TestCase("o3-mini")]
    public async Task OpenAi_LeavesTheTemperatureOut_ForAModelThatRefusesIt(string model)
    {
        var body = await OpenAiRequestBody(model);

        body.TryGetProperty("temperature", out _).Should().BeFalse();
        body.GetProperty("model").GetString().Should().Be(model);
        body.TryGetProperty("max_completion_tokens", out _).Should().BeTrue();
    }

    [TestCase("gpt-6-luna")]
    [TestCase("prod-gpt-6.2-sol")]
    [TestCase("my_gpt5_mini")]
    [TestCase("GPT-5.6-terra-eu")]
    [TestCase("o3-mini")]
    [TestCase("team-o4-mini")]
    public void NamesReasoningModel_WhenTheNameCarriesOne(string deployment)
    {
        OpenAiSampling.NamesReasoningModel(deployment).Should().BeTrue();
    }

    [TestCase("my-gpt-4o")]
    [TestCase("gpt-4.1-prod")]
    [TestCase("gpt-35-turbo")]
    [TestCase("prod-chat")]
    [TestCase("demo1")]
    [TestCase("gpt")]
    [TestCase("")]
    [TestCase(null)]
    public void NamesReasoningModel_IsFalse_ForAnyOtherName(string? deployment)
    {
        OpenAiSampling.NamesReasoningModel(deployment).Should().BeFalse();
    }

    [TestCase("my-gpt-4o")]
    [TestCase("prod-chat")]
    public async Task Azure_SendsTheRequestAsBefore_ToADeploymentThatNamesNoReasoningModel(string deployment)
    {
        var body = await AzureRequestBody(deployment);

        body.TryGetProperty("temperature", out _).Should().BeTrue();
        body.TryGetProperty("max_tokens", out _).Should().BeTrue();
        body.TryGetProperty("max_completion_tokens", out _).Should().BeFalse();
    }

    [TestCase("gpt-6-luna")]
    [TestCase("prod-gpt-5-mini")]
    public async Task Azure_SendsWhatAReasoningModelTakes_ToADeploymentNamedAfterOne(string deployment)
    {
        var body = await AzureRequestBody(deployment);

        body.TryGetProperty("temperature", out _).Should().BeFalse();
        body.TryGetProperty("max_tokens", out _).Should().BeFalse();
        body.GetProperty("max_completion_tokens").GetInt32().Should().Be(256);
    }

    [TestCase("gpt-6.2-astra", 8.00, 40.00)]
    [TestCase("gpt-6.2-sol", 2.00, 10.00)]
    [TestCase("gpt-6.1-terra", 1.00, 5.00)]
    [TestCase("gpt-6-luna", 0.10, 0.50)]
    [TestCase("gpt-4o-mini", 0.15, 0.60)]
    [TestCase("gpt-4o", 5.00, 15.00)]
    public async Task OpenAi_CostsAResponse_AtTheModelsPrices(string model, double inputPerMillion, double outputPerMillion)
    {
        const string million = """{"id":"x","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000000,"completion_tokens":1000000,"total_tokens":2000000}}""";
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, million));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), TestCredentials.ProviderSecretKey, model);

        var response = await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        response.Cost.Should().Be((decimal)inputPerMillion + (decimal)outputPerMillion);
    }

    private static async Task<JsonElement> AzureRequestBody(string deployment)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForAzure(new HttpClient(handler), "https://h.example/", TestCredentials.ProviderSecretKey, deployment);

        await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        return Parse(handler.Requests.Single().Body);
    }

    private static async Task<JsonElement> OpenAiRequestBody(string model)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), TestCredentials.ProviderSecretKey, model);

        await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        return Parse(handler.Requests.Single().Body);
    }

    private static JsonElement Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
