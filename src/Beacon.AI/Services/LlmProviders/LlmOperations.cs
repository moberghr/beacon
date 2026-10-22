namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The <see cref="LlmRequest.Operation"/> names Beacon's own completions carry: constant, low-cardinality values that tag a
/// completion's span and metrics. Never content.
/// </summary>
public static class LlmOperations
{
    public const string AskIntent = "ask.intent";
    public const string AskRouting = "ask.routing";
    public const string AskKnowledgeAnswer = "ask.knowledge_answer";
    public const string AskCrossSourceJoin = "ask.cross_source_join";
    public const string SqlGeneration = "sql.generation";
    public const string SqlSelfConsistency = "sql.self_consistency";
    public const string SqlRepair = "sql.repair";
    public const string DocIndexing = "doc.indexing";
    public const string Learning = "learning.lesson";
    public const string Documentation = "documentation.generate";
    public const string DocumentationInstruct = "documentation.instruct";
    public const string ActorThink = "actor.think";
    public const string ActorRefine = "actor.refine";
    public const string ActorSetup = "actor.setup";
    public const string ActorPlan = "actor.plan";
    public const string EvalJudge = "eval.judge";
    public const string ConnectionTest = "admin.connection_test";
}
