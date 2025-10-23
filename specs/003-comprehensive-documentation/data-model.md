# Data Model: Comprehensive Documentation System

**Date**: 2025-10-22
**Feature**: Comprehensive Documentation System
**Branch**: `003-comprehensive-documentation`

## Purpose

This document defines the logical structure of documentation content entities. Since this is a documentation feature without database schema changes, these entities represent content organization rather than database tables.

## Entity Definitions

### Documentation Section

**Purpose**: Logical grouping of related documentation pages (e.g., Getting Started, Features, Advanced)

**Attributes**:
- `name` (string, required): Section identifier (e.g., "getting-started", "features")
- `title` (string, required): Display name (e.g., "Getting Started", "Features")
- `description` (string, required): Brief section summary for navigation
- `order` (integer, required): Display order in navigation (1-based)
- `icon` (string, optional): Icon identifier for visual navigation
- `pages` (array of DocumentationPage): Child pages in this section

**Relationships**:
- Has many `DocumentationPage` entities
- May reference other sections for cross-linking

**Navigation Structure**:
```
docs/
├── getting-started/     (Section: order=1)
├── features/            (Section: order=2)
├── advanced/            (Section: order=3)
├── api/                 (Section: order=4)
├── troubleshooting/     (Section: order=5)
└── contributing/        (Section: order=6)
```

---

### Documentation Page

**Purpose**: Individual markdown file covering a specific topic

**Attributes**:
- `filename` (string, required): Markdown filename (e.g., "installation.md")
- `path` (string, required): Relative path from docs root (e.g., "getting-started/installation.md")
- `title` (string, required): Page heading (H1)
- `description` (string, required): Brief summary for meta tags and navigation
- `content` (markdown, required): Full page content in GitHub-flavored Markdown
- `lastUpdated` (date, optional): Last modification date
- `author` (string, optional): Primary author or maintainer
- `section` (DocumentationSection, required): Parent section
- `relatedPages` (array of DocumentationPage): Links to related content
- `prerequisites` (array of string): Required knowledge or setup
- `tags` (array of string): Searchable keywords

**Relationships**:
- Belongs to one `DocumentationSection`
- May reference multiple `CodeExample` entities
- May reference multiple `Screenshot` entities
- May reference multiple `UseCase` entities
- May cross-reference other `DocumentationPage` entities

**Front Matter Example**:
```markdown
---
title: "Installation Guide"
description: "Deploy Semantico with Docker Compose in under 10 minutes"
section: getting-started
order: 1
lastUpdated: 2025-10-22
prerequisites:
  - Docker and Docker Compose installed
  - PostgreSQL database available
tags:
  - installation
  - docker
  - deployment
---
```

---

### Code Example

**Purpose**: Reusable code snippet demonstrating a concept

**Attributes**:
- `id` (string, required): Unique identifier (e.g., "docker-compose-basic")
- `language` (string, required): Syntax highlighting hint (e.g., "yaml", "csharp", "bash")
- `code` (string, required): Complete, copy-pasteable code snippet
- `description` (string, required): What this example demonstrates
- `context` (string, optional): When and why to use this pattern
- `usedIn` (array of DocumentationPage): Pages referencing this example
- `validated` (boolean, required): Whether example has been tested

**Example Structure**:
```json
{
  "id": "docker-compose-basic",
  "language": "yaml",
  "code": "services:\n  semantico:\n    image: 'ghcr.io/moberghr/semantico:latest'\n    ports:\n      - 8080:80\n    environment:\n      - ConnectionStrings__SemanticoContext=...",
  "description": "Basic Docker Compose configuration for Semantico",
  "context": "Use this for quick local deployment without SendGrid",
  "validated": true
}
```

**Categories**:
- **Configuration**: Docker Compose, environment variables, connection strings
- **Code**: C# service usage, query definitions, parameter handling
- **Commands**: Bash commands for build, migration, deployment
- **Queries**: SQL examples for multi-step queries, cross-database scenarios

---

### Use Case

**Purpose**: Real-world scenario showing how Semantico solves a problem

**Attributes**:
- `id` (string, required): Unique identifier (e.g., "database-threshold-alerts")
- `title` (string, required): Scenario name (e.g., "Database Threshold Alerts")
- `category` (enum, required): alerting | reporting | migration
- `problem` (string, required): User pain point or business need
- `solution` (string, required): How Semantico addresses the problem
- `benefits` (array of string): Measurable improvements
- `exampleConfig` (CodeExample, optional): Configuration demonstrating solution
- `relatedFeatures` (array of string): Semantico features involved

**Three Use Cases for Marketing Site** (from FR-019):

1. **Database Threshold Alerts** (Category: alerting)
   - Problem: DBAs need proactive alerts when database metrics exceed thresholds
   - Solution: Scheduled query monitoring key metrics (table size, connection count, replication lag) with Teams/email notifications
   - Benefits: Reduced downtime, proactive issue detection, 24/7 monitoring without manual checks
   - Features: Projects, Queries, Subscriptions, Teams notifications

2. **Cross-Database Reporting** (Category: reporting)
   - Problem: Operations teams need consolidated reports from multiple database systems
   - Solution: Multi-step queries aggregating data from PostgreSQL, SQL Server, and MySQL into unified report
   - Benefits: Single pane of glass visibility, automated report generation, consistent formatting
   - Features: Cross-project query chaining, multi-step queries, email notifications with CSV export

3. **Data Migration Orchestration** (Category: migration)
   - Problem: Development teams need auditable data migration tracking across environments
   - Solution: Data migration jobs with execution history and validation checks
   - Benefits: Audit trail for compliance, repeatable migration workflows, error tracking
   - Features: Data migration jobs, execution history, query validation

---

### Screenshot

**Purpose**: Visual asset supporting documentation

**Attributes**:
- `id` (string, required): Unique identifier (e.g., "dashboard-overview")
- `filename` (string, required): Image file (e.g., "dashboard.png")
- `path` (string, required): Relative path from docs root (e.g., "images/dashboard.png")
- `altText` (string, required): Accessibility description
- `caption` (string, required): Brief explanation of what's shown
- `context` (string, optional): When this screenshot is relevant
- `usedIn` (array of DocumentationPage): Pages displaying this screenshot
- `dimensions` (object, optional): `{width: 1200, height: 800}`

**Accessibility Requirements** (WCAG 2.1 Level A):
- All screenshots MUST have descriptive `altText`
- Alt text should describe content and purpose, not just "screenshot of X"
- Decorative images should have empty alt text (`alt=""`)

**Example**:
```markdown
![Main dashboard showing query execution statistics with bar chart of successful vs failed executions over the past 7 days](images/dashboard-overview.png)
*The Semantico dashboard provides at-a-glance visibility into query execution health*
```

---

### Navigation Item

**Purpose**: Menu structure for Jekyll site navigation

**Attributes**:
- `label` (string, required): Display text in navigation menu
- `url` (string, required): Relative URL (e.g., "/getting-started/installation")
- `order` (integer, required): Sort order within parent
- `parent` (NavigationItem, optional): Parent menu item for hierarchical nav
- `children` (array of NavigationItem): Submenu items

**Jekyll Navigation Structure** (_config.yml):
```yaml
nav:
  - title: "Home"
    url: "/"
  - title: "Getting Started"
    url: "/getting-started/"
    children:
      - title: "Installation"
        url: "/getting-started/installation"
      - title: "Quick Start"
        url: "/getting-started/quick-start"
      - title: "Configuration"
        url: "/getting-started/configuration"
  - title: "Features"
    url: "/features/"
    children:
      - title: "Projects"
        url: "/features/projects"
      # ... more feature pages
  # ... more sections
```

---

### Jekyll Configuration

**Purpose**: Site-wide settings for GitHub Pages

**Attributes** (_config.yml):
- `title` (string, required): "Semantico Documentation"
- `description` (string, required): Meta description for SEO
- `baseurl` (string, required): "" (empty for root-level deployment)
- `url` (string, required): "https://moberghr.github.io/semantico"
- `theme` (string, required): "just-the-docs" (or similar)
- `markdown` (string, required): "kramdown"
- `plugins` (array): ["jekyll-feed", "jekyll-seo-tag"]
- `logo` (string, optional): Path to logo image
- `color_scheme` (string, optional): "semantico" (custom color scheme)

**Accessibility Settings**:
```yaml
# WCAG 2.1 Level A compliance
color_scheme: high-contrast
aux_links:
  "GitHub":
    - "https://github.com/moberghr/semantico"
skip_to_content: true  # Skip navigation link for screen readers
```

---

## Content Structure Overview

### README.md (Repository Root)

**Purpose**: GitHub repository landing page, quick overview

**Required Sections** (from FR-001 to FR-008):
1. **Overview** (2-3 sentences): What Semantico does and primary use case
2. **Key Features**: Brief list with links to detailed docs
3. **Quick Start**: Zero to first notification in 30 minutes
4. **Installation**: Docker Compose example
5. **Documentation**: Links to GitHub Pages site
6. **Badges**: Build status, version, license, documentation
7. **Support**: Links to issues, discussions, contributing guide

**Example Structure**:
```markdown
# Semantico

[![Build Status](https://github.com/moberghr/semantico/workflows/CI/badge.svg)](...)
[![Documentation](https://img.shields.io/badge/docs-github.io-blue)](...)

Semantico is a dockerized semantic alerting and notification system that enables...

## 🚀 Quick Start

Get your first database alert running in under 30 minutes:

1. Deploy with Docker Compose
2. Create a project...
3. [View detailed quick start guide →](https://moberghr.github.io/semantico/getting-started/quick-start)

## 📚 Documentation

- [Installation Guide](https://moberghr.github.io/semantico/getting-started/installation)
- [Feature Documentation](https://moberghr.github.io/semantico/features/)
- [API Reference](https://moberghr.github.io/semantico/api/)

## 🤝 Support

- [GitHub Issues](https://github.com/moberghr/semantico/issues)
- [Contributing Guide](https://moberghr.github.io/semantico/contributing/)
```

---

### docs/ Folder Structure

**Complete Directory Layout**:
```
docs/
├── _config.yml                        # Jekyll site configuration
├── index.md                           # Homepage (marketing)
├── Gemfile                            # Jekyll dependencies
├── _layouts/                          # Custom page layouts (optional)
├── _includes/                         # Reusable content snippets (optional)
├── assets/
│   ├── css/
│   │   └── style.css                 # Custom styles for WCAG 2.1
│   ├── images/
│   │   ├── logo.png
│   │   ├── screenshots/              # UI screenshots
│   │   └── diagrams/                 # Architecture diagrams
│   └── downloads/                     # Sample configs, docker-compose files
├── getting-started/
│   ├── index.md                      # Section overview
│   ├── installation.md               # FR-010
│   ├── quick-start.md                # FR-002
│   └── configuration.md              # FR-013
├── features/
│   ├── index.md
│   ├── projects.md                   # FR-011
│   ├── queries.md
│   ├── multi-step-queries.md
│   ├── subscriptions.md
│   ├── notifications.md
│   ├── recipients.md
│   ├── parameters.md
│   └── data-migration.md
├── advanced/
│   ├── index.md
│   ├── query-chaining.md             # FR-016
│   ├── cross-database.md
│   ├── multi-tenant.md               # FR-016
│   ├── custom-providers.md           # FR-016
│   └── architecture.md
├── api/
│   ├── index.md
│   └── services.md                   # FR-014
├── troubleshooting/
│   ├── index.md
│   ├── common-issues.md              # FR-015
│   ├── debugging.md                  # FR-015
│   └── performance.md
└── contributing/
    ├── index.md
    └── guidelines.md
```

---

## Validation Rules

### Code Examples
- MUST be tested and validated (FR-026)
- MUST be complete and copy-pasteable (FR-026)
- MUST include explanatory comments (FR-027)
- MUST use consistent naming conventions (FR-025)

### Documentation Pages
- MUST start with goal and prerequisites (FR-030)
- MUST use consistent terminology (FR-025)
- MUST include screenshots where beneficial (FR-028)
- MUST be written for database-aware users with no Semantico experience (FR-029)

### Accessibility
- All images MUST have descriptive alt text
- Color contrast MUST meet 4.5:1 ratio minimum
- Heading hierarchy MUST be logical (H1 → H2 → H3, no skipping)
- All interactive elements MUST be keyboard accessible

### Site Performance
- Pages MUST load in under 2 seconds (SC-010)
- Images SHOULD be optimized (< 200KB for screenshots)
- Minimize external dependencies

---

## Content Authoring Guidelines

### Terminology Consistency (FR-025)

| Preferred Term | Avoid |
|----------------|-------|
| Query | SQL Query, Database Query |
| Subscription | Schedule, Cron Job |
| Recipient | Target, Destination |
| Notification | Alert, Message |
| Project | Connection, Database |
| Query Step | Query Part, Step |
| Schema-Agnostic | Schema-Independent |

### Example Code Style

All C# examples must follow constitutional guidelines:
- PascalCase for classes, methods, properties
- camelCase for parameters, local variables
- Use `null!` for required string properties
- System namespaces first, then third-party, then project

### Markdown Conventions

- Use ATX-style headings (`#`, `##`, `###`)
- Use fenced code blocks with language hints
- Use relative links for internal navigation
- Use absolute URLs for external resources

---

## Metrics for Success

Each entity type contributes to success criteria:

| Entity | Success Criterion | Target |
|--------|------------------|--------|
| DocumentationPage | SC-001: 30-minute onboarding | Quick start page completable in 30 min |
| NavigationItem | SC-002: 2-minute findability | Clear navigation hierarchy |
| UseCase | SC-005: 70% conversion | 3 compelling use cases on homepage |
| CodeExample | SC-007: Working examples | 100% validated, executable examples |
| Screenshot | SC-008: 5-minute value understanding | Visual feature demonstrations |

---

## Next Steps

1. Create quickstart.md with developer onboarding guide
2. Generate implementation plan with file-by-file breakdown
3. Create tasks.md with phased content creation approach
