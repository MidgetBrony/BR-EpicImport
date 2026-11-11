using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BREpicImport;

public sealed class EpicClient : IDisposable
{
    // Epic Games Launcher OAuth client currently used by Legendary. These are
    // launcher credentials, not a BOXROOM user secret.
    private const string LauncherClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string LauncherClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string OAuthUrl =
        "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    private const string AssetsUrl =
        "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live";
    private const string CatalogHost =
        "https://catalog-public-service-prod06.ol.epicgames.com";
    private const string UserAgent =
        "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";
    private const int StartingAppId = -2000000;

    private readonly HttpClient _client;
    private readonly string _tokenPath;
    private string _boxroomCachePath = "";

    public EpicToken? Token { get; private set; }

    public EpicClient()
    {
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        string dataFolder = Path.Combine(AppContext.BaseDirectory, "EPIC");
        Directory.CreateDirectory(dataFolder);
        _tokenPath = Path.Combine(dataFolder, "token.json");
    }

    public async Task InitializeAsync()
    {
        _boxroomCachePath = BoxroomCacheLocator.Find();
        Console.WriteLine($"Using BOXROOM cache: {_boxroomCachePath}");
        Console.WriteLine(OperatingSystem.IsLinux()
            ? "Epic games will launch through Heroic."
            : "Epic games will launch through Epic Games Launcher.");

        if (!await LoadTokenAsync())
        {
            await LoginAsync();
            return;
        }

        if (Token!.IsExpired)
        {
            try
            {
                await RefreshTokenAsync();
            }
            catch
            {
                Token = null;
                TryDeleteToken();
                Console.WriteLine("The saved Epic sign-in expired. Please sign in again.");
                await LoginAsync();
            }
        }
    }

    public async Task LoginAsync()
    {
        string redirectUrl =
            $"https://www.epicgames.com/id/api/redirect?clientId={LauncherClientId}&responseType=code";
        string loginUrl =
            "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString(redirectUrl);

        Process.Start(new ProcessStartInfo
        {
            FileName = loginUrl,
            UseShellExecute = true
        });

        Console.WriteLine();
        Console.WriteLine("Sign in to Epic in the browser.");
        Console.Write("Paste the authorization code or complete JSON here: ");
        string input = Console.ReadLine() ?? "";
        string code = ExtractAuthorizationCode(input);
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("No Epic authorization code was found.");

        await ExchangeTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["token_type"] = "eg1"
        });

        Console.WriteLine($"Epic sign-in complete for {Token!.DisplayName ?? "your account"}.");
    }

    public async Task ImportOwnedGamesAsync()
    {
        if (Token is null)
            throw new InvalidOperationException("Not signed in to Epic.");

        Console.WriteLine("Reading Epic library...");
        List<EpicAsset> assets = await GetOwnedAssetsAsync();
        Dictionary<string, int> existingImports = LoadExistingImports();
        await UpdateExistingLaunchPathsAsync(existingImports, assets);

        List<EpicAsset> pending = assets
            .Where(asset => !existingImports.ContainsKey(asset.AppName))
            .ToList();

        int alreadyImported = assets.Count - pending.Count;
        int completed = alreadyImported;
        int imported = 0;
        int skipped = 0;
        int failed = 0;
        string? firstFailure = null;
        WriteProgress(completed, assets.Count);

        foreach (EpicAsset asset in pending)
        {
            try
            {
                EpicCatalogItem? catalog = await GetCatalogItemAsync(asset);
                if (catalog is null || !catalog.IsImportableGame)
                {
                    skipped++;
                }
                else
                {
                    await ImportGameAsync(asset, catalog);
                    imported++;
                }
            }
            catch (Exception exception)
            {
                failed++;
                firstFailure ??= $"{asset.AppName}: {exception.Message}";
            }

            completed++;
            WriteProgress(completed, assets.Count);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Complete: {imported} imported, {alreadyImported} already present, {skipped} non-game/DLC entries skipped, {failed} failed.");
        if (firstFailure is not null)
            Console.WriteLine($"First error: {firstFailure}");

        await UpdateOwnedGamesIndexAsync();
    }

    private async Task<List<EpicAsset>> GetOwnedAssetsAsync()
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, AssetsUrl);
        using HttpResponseMessage response = await _client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        EnsureEpicSuccess(response, json, "Epic library request");

        List<EpicAsset> assets = JsonSerializer.Deserialize<List<EpicAsset>>(json) ?? [];
        return assets
            .Where(asset =>
                !string.IsNullOrWhiteSpace(asset.AppName) &&
                !string.IsNullOrWhiteSpace(asset.Namespace) &&
                !string.IsNullOrWhiteSpace(asset.CatalogItemId) &&
                !asset.Namespace.Equals("ue", StringComparison.OrdinalIgnoreCase))
            .GroupBy(asset => asset.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(asset => asset.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<EpicCatalogItem?> GetCatalogItemAsync(EpicAsset asset)
    {
        string url =
            $"{CatalogHost}/catalog/api/shared/namespace/{Uri.EscapeDataString(asset.Namespace)}/bulk/items" +
            $"?id={Uri.EscapeDataString(asset.CatalogItemId)}&includeDLCDetails=true&includeMainGameDetails=true" +
            $"&country={Uri.EscapeDataString(Token?.Country ?? "NO")}&locale=en-US";

        using var request = CreateAuthorizedRequest(HttpMethod.Get, url);
        using HttpResponseMessage response = await _client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        EnsureEpicSuccess(response, json, $"Metadata request for {asset.AppName}");

        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(asset.CatalogItemId, out JsonElement item))
            return null;

        return EpicCatalogItem.FromJson(item);
    }

    private async Task ImportGameAsync(EpicAsset asset, EpicCatalogItem catalog)
    {
        int appId = AllocateAppId();
        SteamMeta metadata = catalog.ToSteamMeta();
        metadata.AppId = appId;
        metadata.AppType = "custom";
        metadata.Source = "epic";
        metadata.StoreId = asset.AppName;
        metadata.LaunchExePath = GetLaunchUri(asset);

        string folder = Path.Combine(_boxroomCachePath, appId.ToString());
        Directory.CreateDirectory(folder);

        if (!string.IsNullOrWhiteSpace(metadata.BoxArtUrlBase))
            await DownloadFileAsync(metadata.BoxArtUrlBase, Path.Combine(folder, "boxart.jpg"));

        for (int index = 0; index < metadata.ScreenshotUrls.Count; index++)
        {
            await DownloadFileAsync(
                metadata.ScreenshotUrls[index],
                Path.Combine(folder, $"screen_{index}.jpg"));
        }

        await File.WriteAllTextAsync(
            Path.Combine(folder, "meta.json"),
            JsonSerializer.Serialize(metadata, JsonOptions.Indented));

        // The helper is the import marker, so write it only after the entry is complete.
        var helper = new MetaHelper
        {
            Type = "Custom",
            Platform = "epic",
            EpicAppName = asset.AppName,
            EpicNamespace = asset.Namespace,
            EpicCatalogItemId = asset.CatalogItemId
        };
        await File.WriteAllTextAsync(
            Path.Combine(folder, "meta.helper.json"),
            JsonSerializer.Serialize(helper, JsonOptions.Indented));
    }

    private async Task<bool> DownloadFileAsync(string url, string destination)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using Stream input = await response.Content.ReadAsStreamAsync();
            await using FileStream output = File.Create(destination);
            await input.CopyToAsync(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<string, int> LoadExistingImports()
    {
        Dictionary<string, int> imports = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_boxroomCachePath))
            return imports;

        foreach (string folder in Directory.GetDirectories(_boxroomCachePath))
        {
            string helperPath = Path.Combine(folder, "meta.helper.json");
            if (!File.Exists(helperPath))
                continue;

            try
            {
                MetaHelper? helper = JsonSerializer.Deserialize<MetaHelper>(File.ReadAllText(helperPath));
                if (helper is null ||
                    !helper.Platform.Equals("epic", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(helper.EpicAppName))
                {
                    continue;
                }

                if (int.TryParse(Path.GetFileName(folder), out int appId))
                    imports.TryAdd(helper.EpicAppName, appId);
            }
            catch
            {
                // Ignore invalid helpers and continue scanning the cache.
            }
        }

        return imports;
    }

    private async Task UpdateExistingLaunchPathsAsync(
        IReadOnlyDictionary<string, int> imports,
        IReadOnlyCollection<EpicAsset> assets)
    {
        Dictionary<string, EpicAsset> assetsByName = assets.ToDictionary(
            asset => asset.AppName,
            StringComparer.OrdinalIgnoreCase);

        foreach ((string appName, int appId) in imports)
        {
            if (!assetsByName.TryGetValue(appName, out EpicAsset? asset))
                continue;

            string metaPath = Path.Combine(_boxroomCachePath, appId.ToString(), "meta.json");
            if (!File.Exists(metaPath))
                continue;

            try
            {
                JsonObject? meta = JsonNode.Parse(await File.ReadAllTextAsync(metaPath)) as JsonObject;
                if (meta is null)
                    continue;

                string launchUri = GetLaunchUri(asset);
                meta["LaunchExePath"] = launchUri;
                meta["AppType"] = "custom";
                meta["Source"] = "epic";
                meta["StoreId"] = asset.AppName;
                await File.WriteAllTextAsync(metaPath, meta.ToJsonString(JsonOptions.Indented));
            }
            catch
            {
                // Leave an invalid existing entry unchanged.
            }
        }
    }

    private async Task UpdateOwnedGamesIndexAsync()
    {
        string path = Path.Combine(_boxroomCachePath, "owned_games.json");
        JsonObject root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject
                ?? throw new InvalidOperationException("BOXROOM's owned_games.json is invalid.")
            : new JsonObject();

        HashSet<int> appIds = [];
        if (root["AppIds"] is JsonArray existingIds)
        {
            foreach (JsonNode? node in existingIds)
            {
                if (node is JsonValue value && value.TryGetValue<int>(out int appId))
                    appIds.Add(appId);
            }
        }

        foreach (int appId in LoadExistingImports().Values)
            appIds.Add(appId);

        root["AppIds"] = new JsonArray(appIds.OrderBy(id => id)
            .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

        string temporaryPath = path + ".epicimport.tmp";
        await File.WriteAllTextAsync(temporaryPath, root.ToJsonString());
        File.Move(temporaryPath, path, true);
    }

    private int AllocateAppId()
    {
        int next = StartingAppId;
        foreach (string directory in Directory.GetDirectories(_boxroomCachePath))
        {
            if (int.TryParse(Path.GetFileName(directory), out int appId) && appId <= next)
                next = appId - 1;
        }
        return next;
    }

    private static string GetLaunchUri(EpicAsset asset)
    {
        if (OperatingSystem.IsLinux())
        {
            return "heroic://launch?appName=" + Uri.EscapeDataString(asset.AppName) +
                   "&runner=legendary";
        }

        return "com.epicgames.launcher://apps/" +
               Uri.EscapeDataString(asset.Namespace) + "%3A" +
               Uri.EscapeDataString(asset.CatalogItemId) + "%3A" +
               Uri.EscapeDataString(asset.AppName) +
               "?action=launch&silent=true";
    }

    private async Task RefreshTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(Token?.RefreshToken))
            throw new InvalidOperationException("The saved Epic token cannot be refreshed.");

        await ExchangeTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = Token.RefreshToken,
            ["token_type"] = "eg1"
        });
        Console.WriteLine("Epic sign-in refreshed.");
    }

    private async Task ExchangeTokenAsync(Dictionary<string, string> values)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthUrl);
        string basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{LauncherClientId}:{LauncherClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(values);

        using HttpResponseMessage response = await _client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        EnsureEpicSuccess(response, json, "Epic sign-in");

        Token = JsonSerializer.Deserialize<EpicToken>(json)
            ?? throw new InvalidOperationException("Epic returned an invalid sign-in response.");
        Token.SavedAt = DateTime.UtcNow;
        await File.WriteAllTextAsync(_tokenPath, JsonSerializer.Serialize(Token, JsonOptions.Indented));
    }

    private async Task<bool> LoadTokenAsync()
    {
        if (!File.Exists(_tokenPath))
            return false;

        try
        {
            Token = JsonSerializer.Deserialize<EpicToken>(await File.ReadAllTextAsync(_tokenPath));
            return Token is not null && !string.IsNullOrWhiteSpace(Token.AccessToken);
        }
        catch
        {
            Token = null;
            TryDeleteToken();
            return false;
        }
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url)
    {
        if (Token is null)
            throw new InvalidOperationException("Not signed in to Epic.");

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token.AccessToken);
        return request;
    }

    private static void EnsureEpicSuccess(HttpResponseMessage response, string content, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        string detail = content;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("errorMessage", out JsonElement message))
                detail = message.GetString() ?? content;
        }
        catch
        {
            // Keep the response text.
        }

        throw new HttpRequestException(
            $"{operation} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {detail}");
    }

    internal static string ExtractAuthorizationCode(string input)
    {
        string value = input.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (document.RootElement.TryGetProperty("authorizationCode", out JsonElement code))
                return code.GetString()?.Trim() ?? "";
        }
        catch (JsonException)
        {
            // The user may have pasted the raw code or a redirect URL.
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            string? code = System.Web.HttpUtility.ParseQueryString(uri.Query)["code"];
            if (!string.IsNullOrWhiteSpace(code))
                return code.Trim();
        }

        Match match = Regex.Match(
            value,
            "\\\"authorizationCode\\\"\\s*:\\s*\\\"(?<code>[^\\\"]+)\\\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["code"].Value.Trim() : value.Trim('"', '\'', ' ');
    }

    private static void WriteProgress(int current, int total) =>
        Console.Write($"\rImporting Epic library: {current} / {total}   ");

    private void TryDeleteToken()
    {
        try
        {
            File.Delete(_tokenPath);
        }
        catch
        {
            // A failed cleanup should not hide the actual sign-in result.
        }
    }

    public void Dispose() => _client.Dispose();
}

public sealed class EpicToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    public DateTime SavedAt { get; set; }

    [JsonIgnore]
    public bool IsExpired => DateTime.UtcNow >= SavedAt.AddSeconds(Math.Max(0, ExpiresIn - 60));
}

public sealed class EpicAsset
{
    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "";

    [JsonPropertyName("catalogItemId")]
    public string CatalogItemId { get; set; } = "";

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = "";
}

internal sealed class EpicCatalogItem
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string LongDescription { get; init; } = "";
    public string Developer { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public List<string> Genres { get; init; } = [];
    public List<EpicImage> Images { get; init; } = [];
    public bool IsDlc { get; init; }
    public bool IsMod { get; init; }

    public bool IsImportableGame => !IsDlc && !IsMod && !string.IsNullOrWhiteSpace(Title);

    public static EpicCatalogItem FromJson(JsonElement item)
    {
        List<string> categories = ReadStringArray(item, "categories", "path");
        List<string> tags = ReadStringArray(item, "tags", "name")
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        List<EpicImage> images = [];
        if (item.TryGetProperty("keyImages", out JsonElement keyImages) &&
            keyImages.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement image in keyImages.EnumerateArray())
            {
                string url = ReadString(image, "url");
                if (!string.IsNullOrWhiteSpace(url))
                    images.Add(new EpicImage(ReadString(image, "type"), url));
            }
        }

        string releaseDate = "";
        if (item.TryGetProperty("releaseInfo", out JsonElement releaseInfo) &&
            releaseInfo.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement release in releaseInfo.EnumerateArray())
            {
                string candidate = ReadString(release, "dateAdded");
                if (string.IsNullOrWhiteSpace(candidate))
                    candidate = ReadString(release, "releaseDate");
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    releaseDate = DateTimeOffset.TryParse(candidate, out DateTimeOffset parsed)
                        ? parsed.ToString("yyyy-MM-dd")
                        : candidate;
                    break;
                }
            }
        }

        return new EpicCatalogItem
        {
            Title = ReadString(item, "title"),
            Description = ReadString(item, "description"),
            LongDescription = ReadString(item, "longDescription"),
            Developer = ReadString(item, "developer"),
            Publisher = ReadString(item, "publisher"),
            ReleaseDate = releaseDate,
            Genres = tags,
            Images = images,
            IsDlc = item.TryGetProperty("mainGameItem", out JsonElement mainGame) &&
                    mainGame.ValueKind == JsonValueKind.Object,
            IsMod = categories.Any(category =>
                category.Equals("mods", StringComparison.OrdinalIgnoreCase))
        };
    }

    public SteamMeta ToSteamMeta()
    {
        string cover = FindImage("DieselGameBoxTall", "OfferImageTall", "Thumbnail");
        string header = FindImage("DieselGameBox", "OfferImageWide", "VaultClosed");
        List<string> screenshots = Images
            .Where(image => image.Type.Contains("Screenshot", StringComparison.OrdinalIgnoreCase))
            .Select(image => image.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return new SteamMeta
        {
            Name = Title,
            ShortDescription = Description,
            DetailedDescription = string.IsNullOrWhiteSpace(LongDescription) ? Description : LongDescription,
            AboutTheGame = string.IsNullOrWhiteSpace(LongDescription) ? Description : LongDescription,
            BoxArtUrlBase = cover,
            FallbackHeaderUrl = header,
            ReleaseDate = ReleaseDate,
            Developers = SplitCompany(Developer),
            Publishers = SplitCompany(Publisher),
            Genres = Genres,
            ScreenshotUrls = screenshots
        };
    }

    private string FindImage(params string[] preferredTypes)
    {
        foreach (string type in preferredTypes)
        {
            string? match = Images.FirstOrDefault(image =>
                image.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Url;
            if (!string.IsNullOrWhiteSpace(match))
                return match;
        }
        return Images.FirstOrDefault()?.Url ?? "";
    }

    private static List<string> SplitCompany(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static List<string> ReadStringArray(
        JsonElement element,
        string propertyName,
        string childProperty)
    {
        List<string> values = [];
        if (!element.TryGetProperty(propertyName, out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement child in array.EnumerateArray())
        {
            string value = ReadString(child, childProperty);
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
        return values;
    }
}

internal sealed record EpicImage(string Type, string Url);

internal sealed class SteamMeta
{
    public int PlayTimeMinutes { get; set; }
    public string Name { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string AppType { get; set; } = "";
    public string DetailedDescription { get; set; } = "";
    public string AboutTheGame { get; set; } = "";
    public string BoxArtUrlBase { get; set; } = "";
    public string FallbackHeaderUrl { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public List<string> Developers { get; set; } = [];
    public List<string> Publishers { get; set; } = [];
    public List<string> Genres { get; set; } = [];
    public List<string> ScreenshotUrls { get; set; } = [];
    public string Source { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string LaunchExePath { get; set; } = "";
    public int AppId { get; set; } = -1;
}

internal sealed class MetaHelper
{
    public string Type { get; set; } = "Custom";
    public string Platform { get; set; } = "epic";
    public string EpicAppName { get; set; } = "";
    public string EpicNamespace { get; set; } = "";
    public string EpicCatalogItemId { get; set; } = "";
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };
}

internal static class BoxroomCacheLocator
{
    private const string CacheFolderName = "steam_cache_v2";
    private const string MarkerFileName = "owned_games.json";

    public static string Find()
    {
        string? configured = Environment.GetEnvironmentVariable("BOXROOM_CACHE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string configuredPath = Path.GetFullPath(configured);
            if (!File.Exists(Path.Combine(configuredPath, MarkerFileName)))
            {
                throw new DirectoryNotFoundException(
                    $"BOXROOM_CACHE_PATH does not point to a BOXROOM cache containing {MarkerFileName}: {configuredPath}");
            }
            return configuredPath;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..", "LocalLow", "NestedLoop", "BOXROOM", CacheFolderName));
        }

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Automatic BOXROOM cache discovery supports Windows and Linux.");

        List<string> matches = [];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddIfBoxroomCache(matches, Path.Combine(
            home, ".config", "unity3d", "NestedLoop", "BOXROOM", CacheFolderName));

        foreach (string steamLibrary in FindSteamLibraries(home))
            FindProtonCaches(steamLibrary, matches);

        return matches
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, MarkerFileName)))
            .FirstOrDefault()
            ?? throw new DirectoryNotFoundException(
                $"Could not find BOXROOM's {MarkerFileName}. Run BOXROOM once, or set BOXROOM_CACHE_PATH.");
    }

    private static IEnumerable<string> FindSteamLibraries(string home)
    {
        string[] steamRoots =
        [
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
        ];

        HashSet<string> libraries = new(StringComparer.Ordinal);
        foreach (string root in steamRoots.Where(Directory.Exists))
        {
            libraries.Add(Path.GetFullPath(root));
            string libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
                continue;

            try
            {
                string vdf = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(
                    vdf,
                    "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"",
                    RegexOptions.IgnoreCase))
                {
                    string path = match.Groups["path"].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(path))
                        libraries.Add(Path.GetFullPath(path));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return libraries;
    }

    private static void FindProtonCaches(string steamLibrary, ICollection<string> matches)
    {
        string compatData = Path.Combine(steamLibrary, "steamapps", "compatdata");
        if (!Directory.Exists(compatData))
            return;

        try
        {
            foreach (string prefix in Directory.EnumerateDirectories(compatData))
            {
                string users = Path.Combine(prefix, "pfx", "drive_c", "users");
                if (!Directory.Exists(users))
                    continue;
                foreach (string user in Directory.EnumerateDirectories(users))
                {
                    AddIfBoxroomCache(matches, Path.Combine(
                        user, "AppData", "LocalLow", "NestedLoop", "BOXROOM", CacheFolderName));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddIfBoxroomCache(ICollection<string> matches, string cachePath)
    {
        if (File.Exists(Path.Combine(cachePath, MarkerFileName)))
            matches.Add(Path.GetFullPath(cachePath));
    }
}

