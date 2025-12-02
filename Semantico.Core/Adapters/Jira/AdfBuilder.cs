using System.Text.Json.Serialization;

namespace Semantico.Core.Adapters.Jira;

/// <summary>
/// Atlassian Document Format (ADF) models and fluent builder.
/// ADF is the native rich text format for Jira Cloud API v3.
/// </summary>
public class AdfBuilder
{
    private readonly List<AdfNode> _content = [];

    /// <summary>
    /// Adds a heading (h1-h6).
    /// </summary>
    public AdfBuilder AddHeading(int level, string text)
    {
        _content.Add(new AdfNode
        {
            Type = "heading",
            Attrs = new Dictionary<string, object> { ["level"] = Math.Clamp(level, 1, 6) },
            Content = [new AdfNode { Type = "text", Text = text }]
        });
        return this;
    }

    /// <summary>
    /// Adds a simple paragraph with plain text.
    /// </summary>
    public AdfBuilder AddParagraph(string text)
    {
        _content.Add(new AdfNode
        {
            Type = "paragraph",
            Content = [new AdfNode { Type = "text", Text = text }]
        });
        return this;
    }

    /// <summary>
    /// Adds a paragraph with rich formatting using a builder.
    /// </summary>
    public AdfBuilder AddParagraph(Action<ParagraphBuilder> configure)
    {
        var builder = new ParagraphBuilder();
        configure(builder);
        _content.Add(new AdfNode
        {
            Type = "paragraph",
            Content = builder.Build()
        });
        return this;
    }

    /// <summary>
    /// Adds a code block with optional language for syntax highlighting.
    /// </summary>
    public AdfBuilder AddCodeBlock(string code, string? language = null)
    {
        var node = new AdfNode
        {
            Type = "codeBlock",
            Content = [new AdfNode { Type = "text", Text = code }]
        };

        if (!string.IsNullOrEmpty(language))
        {
            node.Attrs = new Dictionary<string, object> { ["language"] = language };
        }

        _content.Add(node);
        return this;
    }

    /// <summary>
    /// Adds a horizontal rule (divider line).
    /// </summary>
    public AdfBuilder AddRule()
    {
        _content.Add(new AdfNode { Type = "rule" });
        return this;
    }

    /// <summary>
    /// Adds a bullet list.
    /// </summary>
    public AdfBuilder AddBulletList(IEnumerable<string> items)
    {
        _content.Add(new AdfNode
        {
            Type = "bulletList",
            Content = items.Select(item => new AdfNode
            {
                Type = "listItem",
                Content =
                [
                    new AdfNode
                    {
                        Type = "paragraph",
                        Content = [new AdfNode { Type = "text", Text = item }]
                    }
                ]
            }).ToList()
        });
        return this;
    }

    /// <summary>
    /// Adds a numbered list.
    /// </summary>
    public AdfBuilder AddNumberedList(IEnumerable<string> items)
    {
        _content.Add(new AdfNode
        {
            Type = "orderedList",
            Content = items.Select(item => new AdfNode
            {
                Type = "listItem",
                Content =
                [
                    new AdfNode
                    {
                        Type = "paragraph",
                        Content = [new AdfNode { Type = "text", Text = item }]
                    }
                ]
            }).ToList()
        });
        return this;
    }

    /// <summary>
    /// Adds a blockquote.
    /// </summary>
    public AdfBuilder AddBlockquote(string text)
    {
        _content.Add(new AdfNode
        {
            Type = "blockquote",
            Content =
            [
                new AdfNode
                {
                    Type = "paragraph",
                    Content = [new AdfNode { Type = "text", Text = text }]
                }
            ]
        });
        return this;
    }

    /// <summary>
    /// Adds a table with headers and rows.
    /// </summary>
    public AdfBuilder AddTable(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        var tableContent = new List<AdfNode>();

        // Header row
        var headerRow = new AdfNode
        {
            Type = "tableRow",
            Content = headers.Select(h => new AdfNode
            {
                Type = "tableHeader",
                Content =
                [
                    new AdfNode
                    {
                        Type = "paragraph",
                        Content = [new AdfNode { Type = "text", Text = h }]
                    }
                ]
            }).ToList()
        };
        tableContent.Add(headerRow);

        // Data rows
        foreach (var row in rows)
        {
            var dataRow = new AdfNode
            {
                Type = "tableRow",
                Content = row.Select(cell => new AdfNode
                {
                    Type = "tableCell",
                    Content =
                    [
                        new AdfNode
                        {
                            Type = "paragraph",
                            Content = [new AdfNode { Type = "text", Text = cell ?? "-" }]
                        }
                    ]
                }).ToList()
            };
            tableContent.Add(dataRow);
        }

        _content.Add(new AdfNode
        {
            Type = "table",
            Attrs = new Dictionary<string, object>
            {
                ["isNumberColumnEnabled"] = false,
                ["layout"] = "default"
            },
            Content = tableContent
        });
        return this;
    }

    /// <summary>
    /// Adds an info/note/warning panel.
    /// </summary>
    public AdfBuilder AddPanel(string text, PanelType panelType = PanelType.Info)
    {
        _content.Add(new AdfNode
        {
            Type = "panel",
            Attrs = new Dictionary<string, object> { ["panelType"] = panelType.ToString().ToLower() },
            Content =
            [
                new AdfNode
                {
                    Type = "paragraph",
                    Content = [new AdfNode { Type = "text", Text = text }]
                }
            ]
        });
        return this;
    }

    /// <summary>
    /// Builds the final ADF document.
    /// </summary>
    public AdfDocument Build()
    {
        return new AdfDocument
        {
            Type = "doc",
            Version = 1,
            Content = _content
        };
    }
}

/// <summary>
/// Builder for creating rich paragraph content with inline formatting.
/// </summary>
public class ParagraphBuilder
{
    private readonly List<AdfNode> _content = [];

    /// <summary>
    /// Adds plain text.
    /// </summary>
    public ParagraphBuilder Text(string text)
    {
        _content.Add(new AdfNode { Type = "text", Text = text });
        return this;
    }

    /// <summary>
    /// Adds bold text.
    /// </summary>
    public ParagraphBuilder Bold(string text)
    {
        _content.Add(new AdfNode
        {
            Type = "text",
            Text = text,
            Marks = [new AdfMark { Type = "strong" }]
        });
        return this;
    }

    /// <summary>
    /// Adds italic text.
    /// </summary>
    public ParagraphBuilder Italic(string text)
    {
        _content.Add(new AdfNode
        {
            Type = "text",
            Text = text,
            Marks = [new AdfMark { Type = "em" }]
        });
        return this;
    }

    /// <summary>
    /// Adds inline code.
    /// </summary>
    public ParagraphBuilder Code(string text)
    {
        _content.Add(new AdfNode
        {
            Type = "text",
            Text = text,
            Marks = [new AdfMark { Type = "code" }]
        });
        return this;
    }

    /// <summary>
    /// Adds strikethrough text.
    /// </summary>
    public ParagraphBuilder Strike(string text)
    {
        _content.Add(new AdfNode
        {
            Type = "text",
            Text = text,
            Marks = [new AdfMark { Type = "strike" }]
        });
        return this;
    }

    /// <summary>
    /// Adds a hyperlink.
    /// </summary>
    public ParagraphBuilder Link(string text, string url)
    {
        _content.Add(new AdfNode
        {
            Type = "text",
            Text = text,
            Marks = [new AdfMark { Type = "link", Attrs = new Dictionary<string, object> { ["href"] = url } }]
        });
        return this;
    }

    /// <summary>
    /// Adds a line break (hard break).
    /// </summary>
    public ParagraphBuilder LineBreak()
    {
        _content.Add(new AdfNode { Type = "hardBreak" });
        return this;
    }

    internal List<AdfNode> Build() => _content;
}

public enum PanelType
{
    Info,
    Note,
    Warning,
    Success,
    Error
}

/// <summary>
/// Root ADF document structure.
/// </summary>
public class AdfDocument
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "doc";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("content")]
    public List<AdfNode> Content { get; set; } = [];
}

/// <summary>
/// Generic ADF node that can represent any content type.
/// </summary>
public class AdfNode
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdfNode>? Content { get; set; }

    [JsonPropertyName("attrs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Attrs { get; set; }

    [JsonPropertyName("marks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AdfMark>? Marks { get; set; }
}

/// <summary>
/// ADF mark for inline formatting (bold, italic, links, etc).
/// </summary>
public class AdfMark
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("attrs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Attrs { get; set; }
}
