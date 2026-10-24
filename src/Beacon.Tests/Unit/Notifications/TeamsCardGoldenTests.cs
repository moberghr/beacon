using System.Net;
using System.Text.Json.Nodes;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Teams;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>The exact card the Teams adapter posts for each variant of the default message.</summary>
/// <remarks>Cells are formatted in the current culture, so the goldens pin the invariant one.</remarks>
[TestFixture]
[SetCulture("")]
public class TeamsCardGoldenTests
{
    [Test]
    public async Task Card_WithQueryRowsAndLink()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            Row(("Id", 1), ("Name", "Zagreb \"main\" <b>&</b> č"), ("Created", new DateTime(2026, 3, 9, 14, 30, 15)), ("Ignored", "x")),
            Row(("Id", 2), ("Name", null), ("Created", new DateTime(2026, 3, 10, 1, 2, 3)), ("Ignored", "y")),
            Row(("Id", 3), ("Other", true)),
        };

        var json = await PostedCard(Result(rows, totalRecords: 3, showQuery: true), notificationId: 42, baseUrl: "https://beacon.example.com/");

        AssertCard(json, RowsCard);
    }

    [Test]
    public async Task Card_WithTruncatedRowsNoQueryNoLink()
    {
        var rows = Enumerable.Range(1, 10).Select(x => Row(("N", x))).ToList();

        var json = await PostedCard(Result(rows, totalRecords: 25, showQuery: false), notificationId: null, baseUrl: "https://beacon.example.com");

        AssertCard(json, TruncatedCard);
    }

    [Test]
    public async Task Card_WithNoRows()
    {
        var json = await PostedCard(Result([], totalRecords: 0, showQuery: true), notificationId: 7, baseUrl: "");

        AssertCard(json, EmptyCard);
    }

    [TestCase("not a url")]
    [TestCase("https://user:pw@beacon.example.com")]
    public async Task Card_WithAnUnusableBaseUrl_LeavesTheLinkOutInsteadOfThrowing(string baseUrl)
    {
        var json = await PostedCard(Result([], totalRecords: 0, showQuery: true), notificationId: 7, baseUrl: baseUrl);

        AssertCard(json, EmptyCard);
    }

    [Test]
    public async Task Card_WithALoneSurrogate_WritesTheReplacementCharacter()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            Row(("Text", "a\uD83D b \uDE00c"), ("Number", 1)),
        };

        var json = await PostedCard(Result(rows, totalRecords: 1, showQuery: false), notificationId: 1, baseUrl: "https://beacon.example.com");

        json.Should().Contain("\"a\uFFFD b \uFFFDc\"");
    }

    [Test]
    public async Task Card_WithControlAndUnusualCharacters()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            Row(("Text", "a\nb\tc\\d/e \uD83D\uDE00 \u2028 \u0001 \u007f \u00a0 \u0085 \u2029 \u001f \u000b 'q' +"), ("Number", 1.5m)),
        };

        var json = await PostedCard(Result(rows, totalRecords: 1, showQuery: false), notificationId: 1, baseUrl: "https://beacon.example.com");

        AssertCard(json, UnusualCard);
    }

    [Test]
    public async Task Card_LinkKeepsTheConfiguredAddressAsWritten()
    {
        var json = await PostedCard(Result([], totalRecords: 0, showQuery: false), notificationId: 9, baseUrl: "HTTPS://Beacon.Example.COM/a b/");

        JsonNode.Parse(json)!["actions"]![0]!["url"]!.GetValue<string>().Should().Be("HTTPS://Beacon.Example.COM/a b/notifications/9");
    }

    private static void AssertCard(string posted, string expected)
    {
        var normalizedExpected = expected
            .Replace("@@u", "\\u")
            .Replace("<DEL>", "\u007f")
            .Replace("<NBSP>", "\u00a0")
            .Replace("\r\n", "\n");

        JsonNode.DeepEquals(JsonNode.Parse(posted), JsonNode.Parse(normalizedExpected)).Should().BeTrue();
        posted.Replace("\r\n", "\n").Should().Be(normalizedExpected);
    }

    private static IDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>();
        foreach (var (key, value) in cells)
        {
            row[key] = value;
        }

        return row;
    }

    private static QueryResult Result(List<IDictionary<string, object?>> rows, int totalRecords, bool showQuery)
    {
        return new QueryResult
        {
            QueryResults = "[]",
            TotalRecords = totalRecords,
            DataSourceName = "warehouse \"eu\"",
            SqlQuery = "SELECT * FROM t WHERE a < 3 AND b = 'x' -- š",
            SubscriptionName = "Daily <report>",
            SubscriptionId = 5,
            ShowQuery = showQuery,
            TopRecords = rows,
        };
    }

    private static async Task<string> PostedCard(QueryResult result, int? notificationId, string baseUrl)
    {
        string? body = null;
        var handler = new CapturingHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var sender = new NotificationHttpSender(factory.Object, NullLogger<NotificationHttpSender>.Instance);
        var adapter = new TeamsAdapter(sender, new BeaconConfiguration { BaseUrl = baseUrl }, NullLogger<TeamsAdapter>.Instance);

        await adapter.SendNotificationAsync(
            new RecipientQueryResult
            {
                RecipientDestination = "https://example.webhook.office.com/webhook",
                RecipientNotificationType = NotificationType.Teams,
                QueryResult = result,
                NotificationId = notificationId,
            },
            lastNotificationResultCount: null);

        return body!;
    }

    private const string RowsCard = """
{
  "type": "AdaptiveCard",
  "version": "1.5",
  "speak": "warehouse \"eu\" - Daily <report>",
  "body": [
    {
      "type": "TextBlock",
      "id": "title",
      "size": "large",
      "weight": "bolder",
      "text": "Beacon"
    },
    {
      "type": "TextBlock",
      "id": "subtitle",
      "size": "medium",
      "weight": "bolder",
      "text": "warehouse \"eu\": Daily \\<report>",
      "wrap": true
    },
    {
      "type": "TextBlock",
      "id": "queryText",
      "text": "Query = SELECT \\* FROM t WHERE a \\< 3 AND b = 'x' -- š",
      "wrap": true
    },
    {
      "type": "TextBlock",
      "id": "firstRecordsTitle",
      "weight": "bolder",
      "text": "Query Results",
      "spacing": "medium"
    },
    {
      "type": "Table",
      "rows": [
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "weight": "bolder",
                  "text": "Id"
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "weight": "bolder",
                  "text": "Name"
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "weight": "bolder",
                  "text": "Created"
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "1",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "Zagreb \"main\" \\<b>&\\</b> č",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "2026-03-09 14:30:15",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "2",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "2026-03-10 01:02:03",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "3",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "",
                  "wrap": true
                }
              ]
            }
          ]
        }
      ],
      "columns": [
        {
          "width": 1
        },
        {
          "width": 1
        },
        {
          "width": 1
        }
      ],
      "gridStyle": "emphasis",
      "firstRowAsHeaders": true
    }
  ],
  "actions": [
    {
      "type": "Action.OpenUrl",
      "url": "https://beacon.example.com/notifications/42",
      "title": "View Query Results"
    }
  ]
}
""";

    private const string TruncatedCard = """
{
  "type": "AdaptiveCard",
  "version": "1.5",
  "speak": "warehouse \"eu\" - Daily <report>",
  "body": [
    {
      "type": "TextBlock",
      "id": "title",
      "size": "large",
      "weight": "bolder",
      "text": "Beacon"
    },
    {
      "type": "TextBlock",
      "id": "subtitle",
      "size": "medium",
      "weight": "bolder",
      "text": "warehouse \"eu\": Daily \\<report>",
      "wrap": true
    },
    {
      "type": "TextBlock",
      "id": "firstRecordsTitle",
      "weight": "bolder",
      "text": "First 10 records",
      "spacing": "medium"
    },
    {
      "type": "Table",
      "rows": [
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "weight": "bolder",
                  "text": "N"
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "1",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "2",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "3",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "4",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "5",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "6",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "7",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "8",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "9",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "10",
                  "wrap": true
                }
              ]
            }
          ]
        }
      ],
      "columns": [
        {
          "width": 1
        }
      ],
      "gridStyle": "emphasis",
      "firstRowAsHeaders": true
    }
  ]
}
""";

    private const string EmptyCard = """
{
  "type": "AdaptiveCard",
  "version": "1.5",
  "speak": "warehouse \"eu\" - Daily <report>",
  "body": [
    {
      "type": "TextBlock",
      "id": "title",
      "size": "large",
      "weight": "bolder",
      "text": "Beacon"
    },
    {
      "type": "TextBlock",
      "id": "subtitle",
      "size": "medium",
      "weight": "bolder",
      "text": "warehouse \"eu\": Daily \\<report>",
      "wrap": true
    },
    {
      "type": "TextBlock",
      "id": "queryText",
      "text": "Query = SELECT \\* FROM t WHERE a \\< 3 AND b = 'x' -- š",
      "wrap": true
    },
    {
      "type": "TextBlock",
      "id": "noResultsText",
      "text": "Query executed successfully. Total records: 0",
      "spacing": "medium"
    }
  ]
}
""";

    private const string UnusualCard = """
{
  "type": "AdaptiveCard",
  "version": "1.5",
  "speak": "warehouse \"eu\" - Daily <report>",
  "body": [
    {
      "type": "TextBlock",
      "id": "title",
      "size": "large",
      "weight": "bolder",
      "text": "Beacon"
    },
    {
      "type": "TextBlock",
      "id": "subtitle",
      "size": "medium",
      "weight": "bolder",
      "text": "warehouse \"eu\": Daily \\<report>",
      "wrap": true
    },
    {
      "type": "TextBlock",
      "id": "firstRecordsTitle",
      "weight": "bolder",
      "text": "Query Results",
      "spacing": "medium"
    },
    {
      "type": "Table",
      "rows": [
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "weight": "bolder",
                  "text": "Text"
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "weight": "bolder",
                  "text": "Number"
                }
              ]
            }
          ]
        },
        {
          "type": "TableRow",
          "cells": [
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "a\nb\tc\\\\d/e 😀 @@u2028   <NBSP>  @@u2029   'q' +",
                  "wrap": true
                }
              ]
            },
            {
              "type": "TableCell",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "1.5",
                  "wrap": true
                }
              ]
            }
          ]
        }
      ],
      "columns": [
        {
          "width": 1
        },
        {
          "width": 1
        }
      ],
      "gridStyle": "emphasis",
      "firstRowAsHeaders": true
    }
  ],
  "actions": [
    {
      "type": "Action.OpenUrl",
      "url": "https://beacon.example.com/notifications/1",
      "title": "View Query Results"
    }
  ]
}
""";

    private sealed class CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return respond(request);
        }
    }
}
