using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DroverSwitch.Services;

/// <summary>
/// Looks at the repo's published GitHub releases for the newest tag matching "drover-switch-vX.Y.Z"
/// and compares it against this build's own <see cref="Version"/> csproj property (which also drives
/// AssemblyVersion - see DroverSwitch.csproj). Non-fatal by design: any failure (offline, API rate
/// limit, GitHub down) just means "no update found this time," never an error the user has to deal with.
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/scp-oss/discord-drover/releases";
    private const string TagPrefix = "drover-switch-v";

    public readonly record struct Result(bool HasUpdate, string? Tag, string? ReleaseUrl);

    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // GitHub's API requires a User-Agent on every request, or it rejects with 403.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DroverSwitch-UpdateChecker");

            var json = await http.GetStringAsync(ReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);

            Version? bestVersion = null;
            string? bestTag = null;
            string? bestUrl = null;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (GetBool(release, "draft") || GetBool(release, "prerelease"))
                    continue;

                var tag = GetString(release, "tag_name");
                if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Version.TryParse(tag[TagPrefix.Length..], out var version))
                    continue;

                if (bestVersion is null || version > bestVersion)
                {
                    bestVersion = version;
                    bestTag = tag;
                    bestUrl = GetString(release, "html_url");
                }
            }

            var current = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            if (bestVersion is not null && bestVersion > current && bestUrl is not null)
                return new Result(true, bestTag, bestUrl);

            return default;
        }
        catch
        {
            return default;
        }
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
