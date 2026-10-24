using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Connector.Api.Models;

namespace Beacon.Connector.Api.Services;

/// <summary>
/// Removes an API data source's own credentials (bearer token, API key, Basic password and the Basic header value) from
/// what the API answered, before it reaches the caller: an API that echoes the request URL or headers would otherwise hand
/// the stored credential back as a row. Each credential is matched as sent and in its URL- (upper and lower case hex,
/// <c>%20</c> and <c>+</c>) and JSON-escaped forms. A credential shorter than <see cref="MinSecretLength"/> characters is
/// not scrubbed: replacing a short or common value (a password such as <c>admin</c>, a number) would corrupt keys and
/// unrelated data in the body.
/// </summary>
internal sealed class ApiSecretScrubber
{
    public const string Mask = "***";

    public const int MinSecretLength = 8;

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
            .Where(x => x is { Length: >= MinSecretLength })
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

    private static string LowerHex(string encoded) =>
        Regex.Replace(encoded, "%[0-9A-F]{2}", x => x.Value.ToLowerInvariant());

    private static IEnumerable<string> FormsOf(string secret)
    {
        yield return secret;
        var escaped = Uri.EscapeDataString(secret);
        yield return escaped;
        yield return LowerHex(escaped);
        var formEncoded = WebUtility.UrlEncode(secret);
        yield return formEncoded;
        yield return LowerHex(formEncoded);
        yield return JsonEncodedText.Encode(secret).Value;
        yield return JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value;
    }
}
