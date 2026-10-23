using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Beacon.Connector.Api.Models;

namespace Beacon.Connector.Api.Services;

/// <summary>
/// Removes an API data source's own credentials (bearer token, API key, Basic password and the Basic header value) from
/// what the API answered, before it reaches the caller: an API that echoes the request URL or headers would otherwise hand
/// the stored credential back as a row. Each credential is matched as sent and in its URL- and JSON-escaped forms.
/// </summary>
internal sealed class ApiSecretScrubber
{
    public const string Mask = "***";

    private readonly List<string> _forms;

    private ApiSecretScrubber(List<string> forms)
    {
        _forms = forms;
    }

    public static ApiSecretScrubber For(ApiAuthConfig? auth)
    {
        var secrets = new List<string?>();
        if (auth != null)
        {
            secrets.Add(auth.Token);
            secrets.Add(auth.ApiKeyValue);
            secrets.Add(auth.Password);
            if (!string.IsNullOrEmpty(auth.Username) && !string.IsNullOrEmpty(auth.Password))
            {
                secrets.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}")));
            }
        }

        var forms = secrets
            .Where(x => !string.IsNullOrEmpty(x))
            .SelectMany(x => FormsOf(x!))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(x => x.Length)
            .ToList();

        return new ApiSecretScrubber(forms);
    }

    public string Scrub(string text)
    {
        var result = text;
        foreach (var form in _forms)
        {
            result = result.Replace(form, Mask, StringComparison.Ordinal);
        }

        return result;
    }

    public void Scrub(List<Dictionary<string, object?>> rows)
    {
        if (_forms.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            foreach (var key in row.Keys.ToList())
            {
                if (row[key] is string value)
                {
                    row[key] = Scrub(value);
                }
            }
        }
    }

    private static IEnumerable<string> FormsOf(string secret)
    {
        yield return secret;
        yield return Uri.EscapeDataString(secret);
        yield return JsonEncodedText.Encode(secret).Value;
        yield return JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value;
    }
}
