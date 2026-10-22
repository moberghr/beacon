using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.Internal;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The Bedrock runtime client with its retry handler running <see cref="ClockSkewRetryPolicy"/>: no general retries
/// (the request queue owns those), only the SDK's one re-signed attempt after a clock-skew refusal.
/// </summary>
internal sealed class ClockSkewCorrectingBedrockClient : AmazonBedrockRuntimeClient
{
    public ClockSkewCorrectingBedrockClient(AmazonBedrockRuntimeConfig config)
        : base(config)
    {
    }

    public ClockSkewCorrectingBedrockClient(AWSCredentials credentials, AmazonBedrockRuntimeConfig config)
        : base(credentials, config)
    {
    }

    protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
    {
        base.CustomizeRuntimePipeline(pipeline);
        pipeline.ReplaceHandler<RetryHandler>(new RetryHandler(new ClockSkewRetryPolicy()));
    }
}
