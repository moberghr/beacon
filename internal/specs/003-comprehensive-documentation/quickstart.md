# Quickstart Guide: Implementing Comprehensive Documentation

**Date**: 2025-10-22
**Feature**: Comprehensive Documentation System
**Branch**: `003-comprehensive-documentation`
**Audience**: Developers implementing the documentation system

## Purpose

This guide provides step-by-step instructions for creating the comprehensive documentation system for Beacon. It covers setup, content creation workflow, and validation.

## Prerequisites

- Git access to Beacon repository
- Text editor or IDE for Markdown editing
- Basic understanding of Jekyll and GitHub Pages
- Familiarity with Beacon's features (from research.md)
- Ruby 2.7+ and Bundler (for local Jekyll testing)

## Quick Overview

**Goal**: Create a comprehensive documentation system consisting of:
1. Enhanced README.md in repository root
2. Complete docs/ folder with Jekyll-powered GitHub Pages site
3. 30+ documentation pages covering all features
4. 3 use case examples for marketing homepage
5. WCAG 2.1 Level A accessible site

**Estimated Time**: 40-60 hours of content creation
**Priority**: Follow user story priorities (P1 → P2 → P3 → P4)

---

## Phase 1: Setup GitHub Pages with Jekyll

### Step 1: Create docs/ Folder Structure

```bash
# Navigate to repository root
cd /Users/mirkobudimir/Dev/beacon

# Create directory structure
mkdir -p docs/{getting-started,features,advanced,api,troubleshooting,contributing}
mkdir -p docs/assets/{css,images/screenshots,images/diagrams,downloads}
```

### Step 2: Initialize Jekyll Configuration

Create `docs/_config.yml`:

```yaml
# Site settings
title: "Beacon Documentation"
description: "Comprehensive documentation for Beacon - semantic database alerting and notification system"
baseurl: "" # Leave empty for root deployment
url: "https://moberghr.github.io/beacon"

# Build settings
markdown: kramdown
theme: just-the-docs

# Plugins
plugins:
  - jekyll-feed
  - jekyll-seo-tag

# Theme settings (just-the-docs)
color_scheme: nil  # Use default, or create custom
logo: "/assets/images/logo.png"

# Navigation
nav_external_links:
  - title: GitHub
    url: https://github.com/moberghr/beacon

# Accessibility
aux_links:
  "View on GitHub":
    - "https://github.com/moberghr/beacon"

# SEO
lang: en-US
author: Beacon Team

# Exclude from build
exclude:
  - Gemfile
  - Gemfile.lock
  - README.md
```

### Step 3: Create Gemfile for Local Development

Create `docs/Gemfile`:

```ruby
source "https://rubygems.org"

gem "jekyll", "~> 4.3"
gem "just-the-docs", "~> 0.7"

group :jekyll_plugins do
  gem "jekyll-feed"
  gem "jekyll-seo-tag"
end

# Windows and JRuby does not include zoneinfo files
platforms :mingw, :x64_mingw, :mswin, :jruby do
  gem "tzinfo", ">= 1", "< 3"
  gem "tzinfo-data"
end

# Performance-booster for watching directories
gem "wdm", "~> 0.1", :platforms => [:mingw, :x64_mingw, :mswin]
```

### Step 4: Test Jekyll Locally

```bash
cd docs
bundle install
bundle exec jekyll serve

# Visit http://localhost:4000 to preview
```

### Step 5: Configure GitHub Pages

1. Push changes to `003-comprehensive-documentation` branch
2. Go to GitHub repository → Settings → Pages
3. Set Source to "Deploy from a branch"
4. Set Branch to `main` and folder to `/docs`
5. Save and wait for deployment (typically 1-2 minutes)

**Note**: Pages will deploy when merged to main branch

---

## Phase 2: Create Homepage (Marketing Site)

### Step 1: Create docs/index.md

**Purpose**: Marketing homepage with hero section, features, and use cases (FR-017 to FR-024)

```markdown
---
layout: home
title: Beacon
nav_order: 1
description: "Semantic database alerting and notification system"
permalink: /
---

# Beacon
{: .fs-9 }

Powerful semantic alerts and notifications for your databases
{: .fs-6 .fw-300 }

[Get Started](getting-started/quick-start){: .btn .btn-primary .fs-5 .mb-4 .mb-md-0 .mr-2 }
[View on GitHub](https://github.com/moberghr/beacon){: .btn .fs-5 .mb-4 .mb-md-0 }

---

## Why Beacon?

Beacon transforms database monitoring with semantic queries, flexible alerting, and cross-database orchestration.

- **Multi-Database Support**: PostgreSQL, SQL Server, and MySQL in one unified platform
- **Flexible Alerting**: Email, Microsoft Teams, and Jira notifications with cron scheduling
- **Query Chaining**: Multi-step queries with cross-project and cross-database capabilities
- **Docker-Based**: Deploy in minutes with Docker Compose, no complex infrastructure
- **Notification Channels**: Built-in integrations for Teams, Jira, and email with SendGrid

---

## Use Cases

### 🚨 Database Threshold Alerts

**Problem**: DBAs need proactive alerts when database metrics exceed thresholds

**Solution**: Schedule queries monitoring key metrics (table size, connection count, replication lag) with instant Teams or email notifications

**Benefits**: Reduced downtime, 24/7 monitoring, proactive issue detection

[Learn more about alerting →](features/subscriptions)

---

### 📊 Cross-Database Reporting

**Problem**: Operations teams need consolidated reports from multiple database systems

**Solution**: Multi-step queries aggregate data from PostgreSQL, SQL Server, and MySQL into unified reports with automated delivery

**Benefits**: Single pane of glass visibility, consistent formatting, automated generation

[Learn more about multi-step queries →](features/multi-step-queries)

---

### 🔄 Data Migration Orchestration

**Problem**: Development teams need auditable data migration tracking across environments

**Solution**: Data migration jobs with execution history, validation checks, and error tracking

**Benefits**: Compliance audit trail, repeatable workflows, error visibility

[Learn more about data migrations →](features/data-migration)

---

## Quick Start

Get your first database alert running in under 30 minutes:

1. **Deploy with Docker Compose**
   ```bash
   docker compose up -d
   ```

2. **Access the UI** at `http://localhost:8080/beacon`

3. **Create a project** connecting to your database

4. **Define a query** to monitor

5. **Set up a subscription** with cron schedule

[View detailed quick start guide →](getting-started/quick-start)

---

## Features

- **Projects**: Manage database connections across providers
- **Queries**: Define SQL queries with parameter support
- **Multi-Step Queries**: Chain queries with result aggregation
- **Subscriptions**: Schedule execution with cron expressions
- **Notifications**: Deliver results via email, Teams, or Jira
- **Data Migrations**: Orchestrate and track schema migrations
- **Execution History**: Full audit trail of query execution

[Explore all features →](features/)
```

---

## Phase 3: Create Essential Documentation (P1)

### Priority 1: Developer Quick Start

**Goal**: Enable new users to deploy and create first notification in 30 minutes (SC-001)

#### Create docs/getting-started/installation.md

**Content Requirements** (FR-010):
- Docker Compose deployment steps
- Environment variable configuration
- Connection string formats for PostgreSQL, SQL Server, MySQL
- API key setup (default: 00000000-0000-0000-0000-000000000000)
- Optional SendGrid configuration

**Template**:
```markdown
---
layout: default
title: Installation
parent: Getting Started
nav_order: 1
---

# Installation Guide

Deploy Beacon with Docker Compose in under 10 minutes.

## Prerequisites

- Docker 20.10+ and Docker Compose 2.0+
- PostgreSQL 12+ database for Beacon metadata
- (Optional) SendGrid API key for email notifications

## Quick Installation

### 1. Create docker-compose.yml

```yaml
services:
  beacon:
    image: 'ghcr.io/moberghr/beacon:latest'
    ports:
      - 8080:80
    environment:
      # Required: PostgreSQL connection for Beacon metadata
      - ConnectionStrings__BeaconContext=Host=your-postgres-host;Database=beacon;Username=beacon;Password=your-password

      # Optional: SendGrid for email notifications
      - SendGridSettings__ApiKey=your-sendgrid-api-key
      - SendGridSettings__SenderEmail=alerts@yourdomain.com
      - SendGridSettings__SenderName=Beacon Alerts
```

### 2. Start Beacon

```bash
docker compose up -d
```

### 3. Access the UI

Open your browser to [http://localhost:8080/beacon](http://localhost:8080/beacon)

Default credentials: `admin` / `admin`

## Environment Variables

[Complete reference of all environment variables...]

## Next Steps

- [Quick Start Guide →](quick-start) - Create your first query and notification
- [Configuration Guide →](configuration) - Advanced configuration options
```

#### Create docs/getting-started/quick-start.md

**Content Requirements** (FR-002):
- Step-by-step first query creation
- Subscription setup with cron
- Notification configuration
- Validation of working notification
- Total time: under 30 minutes

**Key Elements**:
1. Create project (database connection)
2. Define simple query (e.g., `SELECT COUNT(*) FROM users WHERE created_at > NOW() - INTERVAL '1 day'`)
3. Test query execution
4. Create subscription with cron (`0 9 * * *` = daily at 9 AM)
5. Add recipient (email or Teams)
6. Trigger manual execution
7. Verify notification received

#### Create docs/getting-started/configuration.md

**Content Requirements** (FR-013):
- All environment variables explained
- Connection string formats for each database provider
- Schema configuration for multi-tenancy
- Notification adapter configuration

---

## Phase 4: Feature Documentation (P2)

### Create Feature Guides

For each major feature, create comprehensive guide (FR-011, FR-012):

**Files to Create**:
1. `docs/features/projects.md` - Project management and database connections
2. `docs/features/queries.md` - Query creation and parameter usage
3. `docs/features/multi-step-queries.md` - Advanced multi-step with aggregation
4. `docs/features/subscriptions.md` - Scheduling with cron expressions
5. `docs/features/notifications.md` - Email, Teams, Jira setup
6. `docs/features/recipients.md` - Recipient management
7. `docs/features/parameters.md` - Dynamic query parameters
8. `docs/features/data-migration.md` - Data migration orchestration

**Template Structure** (FR-012):
```markdown
---
layout: default
title: [Feature Name]
parent: Features
nav_order: [X]
---

# [Feature Name]

[Brief description of what this feature does and why it's valuable]

## Purpose

[2-3 sentences explaining the problem this feature solves]

## Use Cases

- [Use case 1]
- [Use case 2]
- [Use case 3]

## Configuration Steps

### Step 1: [Action]

[Detailed instructions with screenshots]

```code
[Code example]
```

### Step 2: [Action]

[Continue...]

## Examples

### Example 1: [Scenario]

[Complete working example with context]

### Example 2: [Scenario]

[Another example showing variation]

## Tips and Best Practices

- [Tip 1]
- [Tip 2]

## Troubleshooting

[Common issues and solutions]

## Related Documentation

- [Link to related feature 1]
- [Link to related feature 2]
```

---

## Phase 5: Advanced Documentation (P4)

### Create Advanced Guides

**Files to Create**:
1. `docs/advanced/query-chaining.md` - Cross-project query chains (FR-016)
2. `docs/advanced/cross-database.md` - Multi-database query orchestration
3. `docs/advanced/multi-tenant.md` - Schema-agnostic deployments (FR-016)
4. `docs/advanced/custom-providers.md` - Extending database support
5. `docs/advanced/architecture.md` - Clean Architecture deep-dive

**Content Focus**:
- Power-user scenarios
- Complex configurations
- Extensibility patterns
- Performance optimization
- Multi-tenancy patterns

---

## Phase 6: Support Documentation

### Create Troubleshooting Guides

**Files to Create** (FR-015):
1. `docs/troubleshooting/common-issues.md` - FAQ and solutions
2. `docs/troubleshooting/debugging.md` - Logging and diagnostics
3. `docs/troubleshooting/performance.md` - Performance tuning

**Common Issues to Document**:
- Connection failures to databases
- Query execution timeouts
- Notification delivery failures
- Cron expression validation errors
- Migration execution errors
- Hangfire job failures

---

## Content Quality Checklist

Before marking any page complete, verify:

### Code Examples (FR-026, FR-027)
- [ ] All code is complete and copy-pasteable
- [ ] All configuration examples include explanatory comments
- [ ] Examples use consistent naming (PascalCase/camelCase)
- [ ] Examples have been tested and validated

### Writing Style (FR-029, FR-030)
- [ ] Page starts with clear goal statement
- [ ] Prerequisites listed at top
- [ ] Written for database-aware users with no Beacon experience
- [ ] Consistent terminology used (Query, not "SQL Query")

### Accessibility (FR-023)
- [ ] All images have descriptive alt text
- [ ] Headings follow logical hierarchy (H1 → H2 → H3)
- [ ] Color contrast meets 4.5:1 minimum
- [ ] All interactive elements keyboard accessible

### Visual Assets (FR-028)
- [ ] Screenshots included where beneficial
- [ ] Diagrams explain complex concepts
- [ ] Images optimized (< 200KB)
- [ ] Captions provide context

---

## Testing Documentation

### Local Testing

```bash
cd docs
bundle exec jekyll serve

# Test navigation:
# - All links work
# - Table of contents accurate
# - Mobile responsive
# - Load time < 2 seconds
```

### Accessibility Testing

```bash
# Install pa11y
npm install -g pa11y

# Test homepage
pa11y http://localhost:4000

# Test key pages
pa11y http://localhost:4000/getting-started/quick-start
pa11y http://localhost:4000/features/queries
```

### Link Validation

```bash
# Install linkchecker
pip install linkchecker

# Check all internal links
linkchecker http://localhost:4000
```

---

## Deployment Workflow

### 1. Create Pull Request

```bash
# Commit documentation changes
git add docs/ README.md
git commit -m "docs: create comprehensive documentation system

- Add Jekyll-powered GitHub Pages site
- Create getting started guides
- Add feature documentation
- Include 3 use case examples
- Meet WCAG 2.1 Level A standards"

# Push to feature branch
git push origin 003-comprehensive-documentation
```

### 2. Review Checklist

Before merging PR:
- [ ] All pages render correctly locally
- [ ] No broken internal links
- [ ] Accessibility tests pass
- [ ] Code examples validated
- [ ] Screenshots current and clear
- [ ] Mobile responsive verified

### 3. Merge to Main

```bash
# After PR approval
git checkout main
git merge 003-comprehensive-documentation
git push origin main
```

### 4. Verify GitHub Pages Deployment

- Visit https://moberghr.github.io/beacon
- Verify site loads in < 2 seconds (SC-010)
- Test navigation on mobile
- Verify all use cases display correctly

---

## Maintenance Workflow

### Keeping Documentation Current (SC-009)

**Goal**: Documentation lag < 2 weeks after feature releases

**Process**:
1. When feature PR is created, include documentation updates
2. Before merging feature, ensure docs/ updated
3. Update screenshots if UI changes
4. Validate code examples still work
5. Update "Last Updated" dates in front matter

### Monitoring Documentation Quality

**Success Metrics** (from spec):
- Track "how do I..." GitHub issues (target: 60% reduction - SC-004)
- Monitor clicks from homepage to installation guide (target: 70% conversion - SC-005)
- Collect user feedback via GitHub discussions
- Review documentation issues quarterly

---

## Common Pitfalls to Avoid

1. **Outdated Screenshots**: Capture screenshots last, after content stable
2. **Broken Links**: Use relative links for internal navigation
3. **Inconsistent Terminology**: Maintain glossary of preferred terms
4. **Missing Prerequisites**: Always state what users need before starting
5. **Untested Examples**: Validate all code before publishing
6. **Poor Accessibility**: Use alt text and semantic HTML
7. **Overly Technical**: Write for database users, not C# experts

---

## Support and Questions

- **Implementation Questions**: Review research.md and data-model.md
- **Jekyll Issues**: Check [Jekyll documentation](https://jekyllrb.com/docs/)
- **Theme Customization**: See [Just the Docs theme docs](https://just-the-docs.com/)
- **Accessibility**: Consult [WCAG 2.1 guidelines](https://www.w3.org/WAI/WCAG21/quickref/)

---

## Next Steps

1. Start with Phase 1 (Jekyll setup)
2. Create homepage (Phase 2)
3. Focus on P1 documentation (Phase 3)
4. Move through P2, P3, P4 in order
5. Validate against success criteria
6. Deploy and monitor metrics
