# Multi-Agent Documentation Workflow - Visual Diagram

## High-Level Flow

```mermaid
graph TD
    A[User Requests Documentation] --> B[MultiAgentDocumentationService]
    B --> C{Phase 1: Orchestrator}
    C --> D[Analyze Complete Schema]
    D --> E[Identify Domain Groups]
    E --> F{Cache Available?}
    F -->|Yes| G[Use Cached Groupings]
    F -->|No| H[LLM Analysis]
    H --> I[Cache Result]
    I --> J{Phase 2: Domain Agents}
    G --> J

    J --> K[Domain Agent 1<br/>User Management]
    J --> L[Domain Agent 2<br/>Order Processing]
    J --> M[Domain Agent 3<br/>Notifications]
    J --> N[Domain Agent 4<br/>Data Pipeline]
    J --> O[Domain Agent 5<br/>Audit Logs]

    K --> P{Phase 3: Aggregator}
    L --> P
    M --> P
    N --> P
    O --> P

    P --> Q[Combine Results]
    Q --> R[Generate ER Diagram]
    R --> S[Create Executive Summary]
    S --> T[Build Complete Markdown]
    T --> U[Save to Database]
    U --> V[Return Documentation]

    style C fill:#e1f5ff
    style J fill:#fff4e1
    style P fill:#e8f5e9
```

## Detailed Phase Breakdown

### Phase 1: Orchestrator Agent

```mermaid
sequenceDiagram
    participant Client
    participant Service
    participant Cache
    participant LLM
    participant DB

    Client->>Service: GenerateDocumentationAsync()
    Service->>DB: Fetch DataSource & Metadata
    DB-->>Service: Tables, Columns, FKs
    Service->>Cache: Check for cached groupings

    alt Cache Hit
        Cache-->>Service: Return OrchestratorResult
    else Cache Miss
        Service->>LLM: Analyze schema (3000 tokens)
        LLM-->>Service: JSON with domain groups
        Service->>Service: Validate & adjust groups
        Service->>Cache: Store for 60 minutes
    end

    Service->>Client: Progress: "Analyzing Schema" (10%)
```

### Phase 2: Parallel Domain Agents

```mermaid
sequenceDiagram
    participant Service
    participant Semaphore
    participant Agent1
    participant Agent2
    participant Agent3
    participant LLM
    participant Client

    Service->>Semaphore: Acquire (max 5 concurrent)

    par Domain 1
        Service->>Agent1: Document User Management
        Agent1->>LLM: Generate docs (2000 tokens)
        LLM-->>Agent1: JSON result
        Agent1-->>Service: DomainResult
        Service->>Client: Progress: "Domain 1/5" (30%)
    and Domain 2
        Service->>Agent2: Document Order Processing
        Agent2->>LLM: Generate docs (2500 tokens)
        LLM-->>Agent2: JSON result
        Agent2-->>Service: DomainResult
        Service->>Client: Progress: "Domain 2/5" (50%)
    and Domain 3
        Service->>Agent3: Document Notifications
        Agent3->>LLM: Generate docs (1800 tokens)
        LLM-->>Agent3: JSON result
        Agent3-->>Service: DomainResult
        Service->>Client: Progress: "Domain 3/5" (70%)
    end

    Service->>Semaphore: Release all
```

### Phase 3: Aggregator Agent

```mermaid
sequenceDiagram
    participant Service
    participant Aggregator
    participant LLM
    participant DB
    participant Client

    Service->>Client: Progress: "Aggregating Results" (90%)
    Service->>Aggregator: Combine all domain results
    Aggregator->>LLM: Create unified docs (3000 tokens)
    LLM-->>Aggregator: JSON with complete markdown
    Aggregator-->>Service: AggregatedDocumentation

    Service->>DB: Create DataSourceDocumentation
    Service->>DB: Create DocumentationSections
    DB-->>Service: Documentation ID

    Service->>Client: Progress: "Complete" (100%)
    Service->>Client: Return Documentation
```

## Data Flow

```mermaid
graph LR
    A[Database Schema] --> B[Metadata Service]
    B --> C[FilterTables]
    C --> D[Orchestrator Prompt]
    D --> E[Orchestrator LLM Call]
    E --> F[OrchestratorResult]
    F --> G{Split by Domain}

    G --> H1[Domain 1 Tables]
    G --> H2[Domain 2 Tables]
    G --> H3[Domain 3 Tables]

    H1 --> I1[Domain 1 Prompt]
    H2 --> I2[Domain 2 Prompt]
    H3 --> I3[Domain 3 Prompt]

    I1 --> J1[Domain Agent 1]
    I2 --> J2[Domain Agent 2]
    I3 --> J3[Domain Agent 3]

    J1 --> K[DomainResult List]
    J2 --> K
    J3 --> K

    F --> L[Aggregator Prompt]
    K --> L

    L --> M[Aggregator LLM Call]
    M --> N[AggregatedDocumentation]
    N --> O[Parse into Sections]
    O --> P[Save to Database]

    style F fill:#e1f5ff
    style K fill:#fff4e1
    style N fill:#e8f5e9
```

## Component Architecture

```mermaid
classDiagram
    class IMultiAgentDocumentationService {
        <<interface>>
        +GenerateDocumentationAsync()
        +GetCachedOrchestratorResultAsync()
        +ClearOrchestratorCacheAsync()
    }

    class MultiAgentDocumentationService {
        -ILlmProvider llmProvider
        -IDatabaseMetadataService metadataService
        -IMemoryCache cache
        +GenerateDocumentationAsync()
        -RunOrchestratorAsync()
        -ProcessDomainsInParallelAsync()
        -AggregateResultsAsync()
        -SaveDocumentationAsync()
    }

    class MultiAgentPrompts {
        <<static>>
        +GetOrchestratorSystemPrompt()
        +GetDomainAgentSystemPrompt()
        +GetAggregatorSystemPrompt()
        +BuildOrchestratorPrompt()
        +BuildDomainPrompt()
        +BuildAggregatorPrompt()
    }

    class OrchestratorResult {
        +string DatabaseOverview
        +List~DomainGroup~ DomainGroups
        +List~string~ KeyHubTables
        +List~string~ ArchitecturePatterns
    }

    class DomainGroup {
        +string DomainName
        +string Purpose
        +List~string~ Tables
        +int Priority
    }

    class DomainResult {
        +string DomainName
        +string PurposeOverview
        +string CoreTablesDocumentation
        +string Relationships
        +string ExampleQueries
        +string Recommendations
        +string FullMarkdown
        +int TokensUsed
        +decimal EstimatedCost
    }

    class AggregatedDocumentation {
        +string ExecutiveSummary
        +string ArchitectureDiagram
        +List~DomainSection~ DomainSections
        +string CrossDomainRelationships
        +string CompleteMarkdown
        +int TotalTokensUsed
        +decimal TotalEstimatedCost
    }

    class MultiAgentGenerationOptions {
        +int MaxConcurrentAgents
        +int MinTablesPerDomain
        +int MaxDomainsToIdentify
        +decimal Temperature
        +bool EnableOrchestratorCache
    }

    IMultiAgentDocumentationService <|.. MultiAgentDocumentationService
    MultiAgentDocumentationService --> MultiAgentPrompts
    MultiAgentDocumentationService --> OrchestratorResult
    MultiAgentDocumentationService --> DomainResult
    MultiAgentDocumentationService --> AggregatedDocumentation
    MultiAgentDocumentationService --> MultiAgentGenerationOptions
    OrchestratorResult --> DomainGroup
    AggregatedDocumentation --> DomainResult
```

## Token Usage Breakdown

```mermaid
pie title Token Distribution (100-table database, 5 domains)
    "Orchestrator" : 3000
    "Domain Agent 1" : 2000
    "Domain Agent 2" : 2500
    "Domain Agent 3" : 2000
    "Domain Agent 4" : 2200
    "Domain Agent 5" : 1800
    "Aggregator" : 3000
```

## Performance Comparison

```mermaid
gantt
    title Documentation Generation Time Comparison
    dateFormat  s
    axisFormat %S

    section Single-Agent
    Schema Analysis + Documentation :a1, 0, 60s

    section Multi-Agent
    Orchestrator :b1, 0, 8s
    Domain 1 :b2, 8, 12s
    Domain 2 :b3, 8, 12s
    Domain 3 :b4, 8, 12s
    Domain 4 :b5, 8, 12s
    Domain 5 :b6, 8, 12s
    Aggregator :b7, 20, 6s
```

## Error Handling Flow

```mermaid
graph TD
    A[Start Documentation] --> B{Orchestrator Success?}
    B -->|No| C[Log Error & Throw Exception]
    B -->|Yes| D[Start Domain Agents]

    D --> E{Domain 1 Success?}
    D --> F{Domain 2 Success?}
    D --> G{Domain 3 Success?}

    E -->|No| H[Create Error Result for Domain 1]
    E -->|Yes| I[Add Domain 1 Result]

    F -->|No| J[Create Error Result for Domain 2]
    F -->|Yes| K[Add Domain 2 Result]

    G -->|No| L[Create Error Result for Domain 3]
    G -->|Yes| M[Add Domain 3 Result]

    H --> N[Continue with Other Domains]
    I --> N
    J --> N
    K --> N
    L --> N
    M --> N

    N --> O{Aggregator Success?}
    O -->|No| P[Use Fallback Aggregation]
    O -->|Yes| Q[Use LLM Aggregation]

    P --> R[Save Documentation]
    Q --> R
    R --> S[Return Result]

    style C fill:#ffebee
    style H fill:#fff3e0
    style J fill:#fff3e0
    style L fill:#fff3e0
    style P fill:#fff3e0
```

## Cache Strategy

```mermaid
graph LR
    A[Request Documentation] --> B{Cache Enabled?}
    B -->|No| C[Skip Cache Check]
    B -->|Yes| D{Cache Hit?}

    D -->|Yes| E[Check Expiration]
    D -->|No| F[Run Orchestrator]

    E --> G{Expired?}
    G -->|Yes| F
    G -->|No| H[Use Cached Result]

    F --> I[Store in Cache]
    I --> J[Set 60-min TTL]
    J --> K[Continue to Domain Agents]

    H --> K
    C --> F

    style H fill:#c8e6c9
    style I fill:#fff9c4
```

## Progress Updates Timeline

```mermaid
gantt
    title Progress Updates During Generation
    dateFormat  s
    axisFormat %S

    section Progress Events
    Analyzing Schema (10%) :milestone, m1, 0, 0s
    Identified 5 Domains :milestone, m2, 8, 0s
    Domain 1 Complete (26%) :milestone, m3, 12, 0s
    Domain 2 Complete (42%) :milestone, m4, 14, 0s
    Domain 3 Complete (58%) :milestone, m5, 16, 0s
    Domain 4 Complete (74%) :milestone, m6, 18, 0s
    Domain 5 Complete (90%) :milestone, m7, 20, 0s
    Aggregating Results (95%) :milestone, m8, 22, 0s
    Complete (100%) :milestone, m9, 26, 0s
```
