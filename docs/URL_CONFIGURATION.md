---
nav_exclude: true
---

# URL Configuration Guide

This guide explains how to use the centralized URL configuration system in Beacon documentation.

## Overview

Instead of hardcoding URLs throughout the documentation, we use a centralized configuration in `_config.yml`. This makes it easier to:

- **Update URLs**: Change a URL once instead of hunting through multiple files
- **Maintain consistency**: Ensure all references to the same resource use the same URL
- **Reduce errors**: Prevent typos and broken links
- **Improve maintainability**: Makes the documentation easier to update

## Configuration Location

All URLs are defined in `docs/_config.yml` under the `urls` section:

```yaml
# Centralized URLs - define URLs once, reference throughout documentation
urls:
  # Repository
  github_repo: "https://github.com/moberghr/beacon"
  github_issues: "https://github.com/moberghr/beacon/issues"
  github_discussions: "https://github.com/moberghr/beacon/discussions"

  # Documentation
  docs_base: "https://moberghr.github.io/beacon"
  docs_installation: "https://moberghr.github.io/beacon/getting-started/installation"
  docs_quick_start: "https://moberghr.github.io/beacon/getting-started/quick-start"
  # ... more URLs
```

## Usage in Markdown Files

To use a configured URL in your markdown documentation, use Jekyll's Liquid template syntax:

### Basic Link

```markdown
[View on GitHub]({{ site.urls.github_repo }})
```

Renders as: `[View on GitHub](https://github.com/moberghr/beacon)`

### Button with URL

```markdown
[Get Started]({{ site.urls.docs_quick_start }}){: .btn .btn-primary }
```

### In Text

```markdown
For more information, visit our [documentation]({{ site.urls.docs_base }}).
```

## Examples

### Repository Links

```markdown
- **Issues**: [Report bugs]({{ site.urls.github_issues }})
- **Discussions**: [Ask questions]({{ site.urls.github_discussions }})
- **GitHub**: [View source]({{ site.urls.github_repo }})
```

### Documentation Links

```markdown
- [Installation Guide]({{ site.urls.docs_installation }})
- [Quick Start]({{ site.urls.docs_quick_start }})
- [Configuration]({{ site.urls.docs_configuration }})
```

### Mixed Content

```markdown
## Getting Help

Need assistance? Check out:
- [Documentation]({{ site.urls.docs_base }}) - Complete guides and references
- [GitHub Issues]({{ site.urls.github_issues }}) - Report bugs or request features
- [Discussions]({{ site.urls.github_discussions }}) - Community support
```

## Adding New URLs

To add a new URL to the configuration:

1. Open `docs/_config.yml`
2. Navigate to the `urls` section
3. Add your new URL with a descriptive key:

```yaml
urls:
  # ... existing URLs
  docs_api_reference: "https://moberghr.github.io/beacon/api/reference"
```

4. Use it in your documentation:

```markdown
[API Reference]({{ site.urls.docs_api_reference }})
```

## URL Categories

The configuration is organized into logical categories:

- **Repository**: GitHub-related URLs (repo, issues, discussions)
- **Documentation**: Internal documentation pages
- **NuGet**: Package repository URLs
- **External**: Third-party resources (.NET, etc.)

## Best Practices

1. **Use descriptive keys**: `docs_quick_start` is better than `qs` or `qsdocs`
2. **Group related URLs**: Keep similar URLs together in the config
3. **Always use absolute URLs**: Include the full URL with protocol (`https://`)
4. **Document new URLs**: Add comments in `_config.yml` for clarity
5. **Check before adding**: Search the config to avoid duplicates

## Benefits

- ✅ **Single source of truth** for all external and internal links
- ✅ **Easy refactoring** when URLs change
- ✅ **Consistent formatting** across all documentation pages
- ✅ **Reduced maintenance** burden
- ✅ **Fewer broken links** due to typos

## Testing URLs

After adding or modifying URLs, test them:

1. Build the site locally:
   ```bash
   cd docs
   bundle exec jekyll serve
   ```

2. Open `http://localhost:4001/beacon/`

3. Click through links to verify they work correctly

## Troubleshooting

### Link doesn't render

**Problem**: `{{ site.urls.github_repo }}` appears as plain text

**Solution**: Ensure you're using the correct Liquid syntax with double curly braces

### URL not found

**Problem**: Link renders as empty or broken

**Solution**:
1. Check spelling of the URL key in `_config.yml`
2. Verify the key exists under the `urls` section
3. Rebuild the Jekyll site (`Ctrl+C` and re-run `jekyll serve`)

### Changes not appearing

**Problem**: URL updates don't show after editing `_config.yml`

**Solution**: Restart Jekyll server - changes to `_config.yml` require a restart

---

**Note**: For relative internal links within the documentation (like `/getting-started/installation`), you can continue using standard markdown links. The centralized URL configuration is primarily for external links and absolute URLs to documentation pages.
