using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using SmartFactory.Services.DeviceService.Application.DTOs;

namespace SmartFactory.Services.DeviceService.Application.Services;

public sealed class AasProvisioningConfigurationException(string message) : Exception(message);
public sealed class AasVersionConflictException(string message) : Exception(message);

/// <summary>Registers generated models with a private IDTA AAS Repository and Registry.</summary>
public sealed partial class AasProvisioningService(HttpClient http, IConfiguration configuration, IHostEnvironment environment, ILogger<AasProvisioningService> logger)
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? cachedToken;
    private static string? cachedKey;
    private static DateTimeOffset tokenExpiresAt;

    public async Task<Dictionary<string, object?>> CreateAsync(AssetProvisionRequest input, CancellationToken ct)
    {
        var documents = AasDocumentsBuilder.Build(input);
        var submodelsCreated = new List<string>();
        var shellCreated = false;
        var descriptorCreated = false;
        try
        {
            foreach (var submodel in documents.Submodels)
            {
                var id = (string)submodel["id"]!;
                await SendJsonAsync(HttpMethod.Post, RepositoryUri("submodels"), submodel, ct);
                submodelsCreated.Add(id);
            }
            await SendJsonAsync(HttpMethod.Post, RepositoryUri("shells"), documents.Shell, ct);
            shellCreated = true;
            if (!RepositoryManagesRegistry)
            {
                await SendJsonAsync(HttpMethod.Post, RegistryUri("shell-descriptors"), BuildDescriptor(input), ct);
                descriptorCreated = true;
            }
            return Result(documents);
        }
        catch (Exception error)
        {
            // A disconnected caller must not cancel compensation of confirmed writes.
            // Bound cleanup separately so an unavailable repository cannot hold the request forever.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await TryRollbackAsync(input.AssetId, submodelsCreated, shellCreated, descriptorCreated, recovery.Token);
            if (error is OperationCanceledException) throw;
            throw new InvalidOperationException("AAS repository or registry registration failed.", error);
        }
    }

    public async Task<Dictionary<string, object?>> UpdateAsync(AssetProvisionRequest input, CancellationToken ct)
    {
        if (input.ExpectedAasVersion is null || input.AasVersion != input.ExpectedAasVersion.Value + 1)
            throw new AasVersionConflictException("AAS update must include the next revision and expected current revision.");

        var documents = AasDocumentsBuilder.Build(input);
        var currentSubmodels = new List<PreviousAasResource>();
        foreach (var submodel in documents.Submodels)
        {
            var id = (string)submodel["id"]!;
            var current = await ReadCurrentResourceAsync(RepositoryUri($"submodels/{EncodeId(id)}"), ct);
            var idShort = (string)submodel["idShort"]!;
            if (!HasExpectedRevision(current.Payload, idShort, input.ExpectedAasVersion.Value))
                throw new AasVersionConflictException($"AAS submodel {idShort} has changed since revision {input.ExpectedAasVersion.Value}.");
            currentSubmodels.Add(current);
        }

        var shell = await ReadCurrentResourceAsync(RepositoryUri($"shells/{EncodeId(input.AssetId)}"), ct);
        if (!HasExpectedRevision(shell.Payload, "AssetAdministrationShell", input.ExpectedAasVersion.Value))
            throw new AasVersionConflictException("AAS shell has changed since the expected revision.");
        var descriptor = await ReadCurrentResourceAsync(RegistryUri($"shell-descriptors/{EncodeId(input.AssetId)}"), ct);

        var updated = new List<PreviousAasResource>();
        try
        {
            for (var index = 0; index < documents.Submodels.Count; index++)
            {
                var model = documents.Submodels[index];
                await SendJsonAsync(HttpMethod.Put, currentSubmodels[index].Uri, model, ct);
                updated.Add(currentSubmodels[index]);
            }
            await SendJsonAsync(HttpMethod.Put, shell.Uri, documents.Shell, ct);
            updated.Add(shell);
            if (!RepositoryManagesRegistry)
            {
                await SendJsonAsync(HttpMethod.Put, descriptor.Uri, BuildDescriptor(input), ct);
                updated.Add(descriptor);
            }
        }
        catch (Exception error)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var resource in updated.AsEnumerable().Reverse())
            {
                try { await SendJsonAsync(HttpMethod.Put, resource.Uri, resource.Payload, recovery.Token); }
                catch (Exception rollbackError) { logger.LogError(rollbackError, "AAS revision rollback failed for {ResourcePath}.", resource.Uri.AbsolutePath); }
            }
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed })
                throw new AasVersionConflictException("AAS repository rejected a stale revision update.");
            throw;
        }
        return Result(documents);
    }

    private sealed record PreviousAasResource(Uri Uri, JsonElement Payload);

    private async Task<PreviousAasResource> ReadCurrentResourceAsync(Uri uri, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddBearerIfPresent(request, token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new AasVersionConflictException("The expected AAS resource no longer exists in the repository.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Configured AAS service returned HTTP {(int)response.StatusCode} while reading the current revision.", null, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return new PreviousAasResource(uri, document.RootElement.Clone());
    }

    private static bool HasExpectedRevision(JsonElement resource, string idShort, int expectedVersion)
    {
        if (!resource.TryGetProperty("administration", out var administration) ||
            !administration.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String ||
            !int.TryParse(versionElement.GetString(), out var actualVersion))
            return idShort == "AssetAdministrationShell" && expectedVersion == 1;

        if (actualVersion == expectedVersion) return true;
        // Accept the original template-major values once for assets created
        // before this revision ledger was introduced.
        var legacyTemplateVersion = idShort switch { "Nameplate" => 3, "TechnicalData" => 2, "MaintenanceInstructions" => 1, _ => 0 };
        return expectedVersion == 1 && actualVersion == legacyTemplateVersion;
    }

    public async Task<Dictionary<string, object?>> DeleteAsync(AssetProvisionRequest input, CancellationToken ct)
    {
        await DeleteIfPresentAsync(RegistryUri($"shell-descriptors/{EncodeId(input.AssetId)}"), ct);
        await DeleteIfPresentAsync(RepositoryUri($"shells/{EncodeId(input.AssetId)}"), ct);
        foreach (var name in new[] { "Nameplate", "TechnicalData", "MaintenanceInstructions" })
            await DeleteIfPresentAsync(RepositoryUri($"submodels/{EncodeId($"{input.AssetId}/submodels/{name}")}"), ct);
        return Result(AasDocumentsBuilder.Build(input));
    }

    private static string EncodeId(string id) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(id));
    private static Dictionary<string, object?> Result(AasDocuments documents) => new()
    {
        ["shell"] = documents.Shell, ["submodels"] = documents.Submodels,
        ["repositoryRegistered"] = true, ["registryRegistered"] = true,
    };

    private Dictionary<string, object?> BuildDescriptor(AssetProvisionRequest input)
    {
        var repository = ConfiguredUri("AAS_REPOSITORY_URL");
        return new Dictionary<string, object?>
        {
            ["id"] = input.AssetId, ["globalAssetId"] = input.AssetId,
            ["specificAssetIds"] = new[] { new Dictionary<string, object?> { ["name"] = "assetType", ["value"] = input.AssetType } },
            ["endpoints"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["interface"] = "AAS-3.2",
                    ["protocolInformation"] = new Dictionary<string, object?>
                    {
                        ["href"] = repository.ToString().TrimEnd('/'),
                        ["endpointProtocol"] = repository.Scheme == "https" ? "HTTPS" : "HTTP",
                        ["endpointProtocolVersion"] = new[] { "1.1" }, ["securityAttributes"] = Array.Empty<object>(),
                    },
                },
            },
        };
    }

    private async Task SendJsonAsync(HttpMethod method, Uri uri, object payload, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        AddBearerIfPresent(request, token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Configured AAS service returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private async Task DeleteIfPresentAsync(Uri uri, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        AddBearerIfPresent(request, token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException($"Configured AAS service returned HTTP {(int)response.StatusCode} during cleanup.", null, response.StatusCode);
    }

    private async Task TryRollbackAsync(string assetId, IReadOnlyCollection<string> submodels, bool shellCreated, bool descriptorCreated, CancellationToken ct)
    {
        try
        {
            if (descriptorCreated) await DeleteIfPresentAsync(RegistryUri($"shell-descriptors/{EncodeId(assetId)}"), ct);
            if (shellCreated) await DeleteIfPresentAsync(RepositoryUri($"shells/{EncodeId(assetId)}"), ct);
            foreach (var id in submodels.Reverse()) await DeleteIfPresentAsync(RepositoryUri($"submodels/{EncodeId(id)}"), ct);
        }
        catch (Exception error) { logger.LogError(error, "AAS rollback failed after partial registration."); }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (CanUseUnauthenticatedLocalAas()) return string.Empty;
        var directTokenUrl = configuration["AAS_OIDC_TOKEN_URL"];
        var discoveryUrl = configuration["AAS_OIDC_DISCOVERY_URL"];
        if (string.IsNullOrWhiteSpace(directTokenUrl) && string.IsNullOrWhiteSpace(discoveryUrl))
            throw new AasProvisioningConfigurationException("AAS_OIDC_TOKEN_URL or AAS_OIDC_DISCOVERY_URL is required for AAS provisioning.");
        var identitySetting = string.IsNullOrWhiteSpace(directTokenUrl) ? discoveryUrl! : directTokenUrl;
        var scope = configuration["AAS_OIDC_SCOPE"]?.Trim();
        var clientId = RequiredSetting("AAS_OIDC_CLIENT_ID");
        var secret = RequiredSetting("AAS_OIDC_CLIENT_SECRET");
        var key = $"{identitySetting}\n{scope}\n{clientId}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))}";
        if (cachedToken is not null && cachedKey == key && tokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return cachedToken;
        await TokenLock.WaitAsync(ct);
        try
        {
            if (cachedToken is not null && cachedKey == key && tokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return cachedToken;
            var tokenUrl = string.IsNullOrWhiteSpace(directTokenUrl)
                ? await DiscoverTokenEndpointAsync(discoveryUrl!, ct)
                : ConfiguredUriValue("AAS_OIDC_TOKEN_URL", directTokenUrl);
            var fields = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret };
            if (!string.IsNullOrWhiteSpace(scope)) fields["scope"] = scope;
            using var form = new FormUrlEncodedContent(fields);
            using var response = await http.PostAsync(tokenUrl, form, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AAS identity provider returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            if (!root.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidOperationException("AAS identity provider returned an invalid token response.");
            var expires = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) ? Math.Max(30, seconds) : 300;
            cachedToken = value.GetString(); cachedKey = key; tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires);
            return cachedToken!;
        }
        finally { TokenLock.Release(); }
    }

    private async Task<Uri> DiscoverTokenEndpointAsync(string discoveryUrl, CancellationToken ct)
    {
        var discoveryUri = ConfiguredUriValue("AAS_OIDC_DISCOVERY_URL", discoveryUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AAS OIDC discovery returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!metadata.RootElement.TryGetProperty("token_endpoint", out var endpoint) || endpoint.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(endpoint.GetString()))
            throw new AasProvisioningConfigurationException("AAS OIDC discovery document has no token_endpoint.");
        return ConfiguredUriValue("AAS OIDC token_endpoint", endpoint.GetString()!);
    }

    private Uri RepositoryUri(string path)
    {
        var first = path.Split('/', 2)[0];
        var baseSetting = first switch
        {
            "submodels" => "AAS_SUBMODEL_REPOSITORY_URL",
            "concept-descriptions" => "AAS_CONCEPT_DESCRIPTION_REPOSITORY_URL",
            _ => "AAS_REPOSITORY_URL",
        };
        return JoinBase(ConfiguredUri(baseSetting), path);
    }

    private Uri RegistryUri(string path) => JoinBase(ConfiguredUri("AAS_REGISTRY_URL"), path);
    private static Uri JoinBase(Uri baseUri, string path) => new($"{baseUri.ToString().TrimEnd('/')}/{path}", UriKind.Absolute);

    private bool RepositoryManagesRegistry => bool.TryParse(configuration["AAS_REPOSITORY_REGISTRY_INTEGRATION"], out var enabled) && enabled;

    private static void AddBearerIfPresent(HttpRequestMessage request, string token)
    {
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private bool CanUseUnauthenticatedLocalAas()
    {
        var developmentOverride = environment.IsDevelopment()
            && bool.TryParse(configuration["AAS_ALLOW_UNAUTHENTICATED_LOCAL"], out var allowLocal)
            && allowLocal;
        var privateDeploymentOverride = bool.TryParse(configuration["AAS_ALLOW_UNAUTHENTICATED_PRIVATE"], out var allowPrivate)
            && allowPrivate;
        if (!developmentOverride && !privateDeploymentOverride) return false;
        var settings = new[]
        {
            configuration["AAS_REPOSITORY_URL"] ?? configuration["AAS_REPO_URL"],
            configuration["AAS_SUBMODEL_REPOSITORY_URL"], configuration["AAS_CONCEPT_DESCRIPTION_REPOSITORY_URL"],
            configuration["AAS_REGISTRY_URL"], configuration["AAS_SUBMODEL_REGISTRY_URL"], configuration["AASX_FILE_SERVER_URL"],
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (settings.Length < 2) return false;

        var approvedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", "127.0.0.1", "::1", "aas-repository", "submodel-repository",
            "concept-description-repository", "aas-registry", "submodel-registry", "aasx-file-server",
        };
        return settings.All(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" && approvedHosts.Contains(uri.Host));
    }

    private Uri ConfiguredUri(string name)
    {
        var value = name == "AAS_REPOSITORY_URL"
            ? configuration[name] ?? configuration["AAS_REPO_URL"] ?? throw new AasProvisioningConfigurationException("AAS_REPOSITORY_URL or AAS_REPO_URL is required for AAS provisioning.")
            : configuration[name] ?? throw new AasProvisioningConfigurationException($"{name} is required for AAS provisioning.");
        return ConfiguredUriValue(name, value);
    }

    private Uri ConfiguredUriValue(string name, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new AasProvisioningConfigurationException($"{name} must be an HTTP(S) URL without credentials, query, or fragment.");
        if (environment.IsProduction() && uri.Scheme != "https" && !CanUseUnauthenticatedLocalAas())
            throw new AasProvisioningConfigurationException($"{name} must use HTTPS in production unless every AAS endpoint is an approved private BaSyx service.");
        return uri;
    }

    private string RequiredSetting(string name) => !string.IsNullOrWhiteSpace(configuration[name])
        ? configuration[name]!
        : throw new AasProvisioningConfigurationException($"{name} is required for AAS provisioning.");
}
