using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using SmartFactory.Services.DeviceService.API.Controllers;

namespace SmartFactory.Services.DeviceService.Application.Services;

public sealed partial class AasProvisioningService
{
    public async Task<Dictionary<string, object?>> ImportAasxAsync(byte[] package, string fileName, CancellationToken ct)
    {
        var environment = AasxPackageParser.Parse(package);
        var shells = environment["assetAdministrationShells"] as JsonArray ?? throw new InvalidDataException("AAS environment contains no shells.");
        var submodels = environment["submodels"] as JsonArray ?? [];
        var concepts = environment["conceptDescriptions"] as JsonArray ?? [];
        if (shells.Count is < 1 or > 25) throw new InvalidDataException("An AASX package must contain 1 to 25 shells.");

        var shellIds = shells.Select(node => node?["id"]?.GetValue<string>()).ToArray();
        var submodelIds = submodels.Select(node => node?["id"]?.GetValue<string>()).ToArray();
        var conceptIds = concepts.Select(node => node?["id"]?.GetValue<string>()).ToArray();
        if (shellIds.Any(string.IsNullOrWhiteSpace) || shellIds.Distinct(StringComparer.Ordinal).Count() != shellIds.Length ||
            submodelIds.Any(string.IsNullOrWhiteSpace) || submodelIds.Distinct(StringComparer.Ordinal).Count() != submodelIds.Length ||
            conceptIds.Any(string.IsNullOrWhiteSpace) || conceptIds.Distinct(StringComparer.Ordinal).Count() != conceptIds.Length)
            throw new InvalidDataException("AAS package contains missing or duplicate global identifiers.");

        var createdSubmodels = new List<string>();
        var createdConcepts = new List<string>();
        var createdShells = new List<string>();
        var createdDescriptors = new List<string>();
        string? packageId = null;
        try
        {
            foreach (var model in submodels)
            {
                var id = model!["id"]!.GetValue<string>();
                await SendJsonAsync(HttpMethod.Post, RepositoryUri("submodels"), model, ct);
                createdSubmodels.Add(id);
            }
            foreach (var concept in concepts)
            {
                var id = concept!["id"]!.GetValue<string>();
                await SendJsonAsync(HttpMethod.Post, RepositoryUri("concept-descriptions"), concept, ct);
                createdConcepts.Add(id);
            }
            foreach (var shell in shells)
            {
                var id = shell!["id"]!.GetValue<string>();
                await SendJsonAsync(HttpMethod.Post, RepositoryUri("shells"), shell, ct);
                createdShells.Add(id);
                if (!RepositoryManagesRegistry)
                {
                    await SendJsonAsync(HttpMethod.Post, RegistryUri("shell-descriptors"), BuildImportedDescriptor(shell), ct);
                    createdDescriptors.Add(id);
                }
            }
            packageId = await UploadAasxAsync(package, fileName, shellIds.Select(id => id!).ToArray(), ct);
            return new Dictionary<string, object?>
            {
                ["packageId"] = packageId,
                ["assetAdministrationShells"] = shells,
                ["submodels"] = submodels,
                ["conceptDescriptions"] = concepts,
            };
        }
        catch (Exception error)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await RollbackRecordsAsync(createdDescriptors, createdShells, createdSubmodels, createdConcepts, recovery.Token);
            if (packageId is not null) await DeletePackageIfPresentAsync(packageId, recovery.Token);
            if (error is OperationCanceledException) throw;
            throw new InvalidOperationException("AASX repository or package-service registration failed.", error);
        }
    }

    public async Task RollbackAasxAsync(AasxImportReceipt receipt, CancellationToken ct)
    {
        await RollbackRecordsAsync(receipt.AssetIds, receipt.AssetIds, receipt.SubmodelIds, receipt.ConceptDescriptionIds, ct);
        await DeletePackageIfPresentAsync(receipt.PackageId, ct);
    }

    private Dictionary<string, object?> BuildImportedDescriptor(JsonNode shell)
    {
        var id = shell["id"]?.GetValue<string>() ?? throw new InvalidDataException("AAS shell is missing its identifier.");
        var globalAssetId = shell["assetInformation"]?["globalAssetId"]?.GetValue<string>();
        var repository = ConfiguredUri("AAS_REPOSITORY_URL");
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["globalAssetId"] = globalAssetId,
            ["endpoints"] = new[] { new Dictionary<string, object?>
            {
                ["interface"] = "AAS-3.2",
                ["protocolInformation"] = new Dictionary<string, object?>
                {
                    ["href"] = repository.ToString().TrimEnd('/'),
                    ["endpointProtocol"] = repository.Scheme == "https" ? "HTTPS" : "HTTP",
                    ["endpointProtocolVersion"] = new[] { "1.1" }, ["securityAttributes"] = Array.Empty<object>(),
                },
            } },
        };
    }

    private async Task<string> UploadAasxAsync(byte[] package, string fileName, string[] shellIds, CancellationToken ct)
    {
        var packageService = JoinBase(ConfiguredUri("AASX_FILE_SERVER_URL"), "packages");
        var safeFileName = Path.GetFileName(fileName.Replace('\\', '/'));
        var token = await GetAccessTokenAsync(ct);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/asset-administration-shell-package");
        content.Add(file, "file", safeFileName);
        foreach (var shellId in shellIds) content.Add(new StringContent(shellId, Encoding.UTF8), "aasIds");
        content.Add(new StringContent(safeFileName, Encoding.UTF8), "fileName");
        using var request = new HttpRequestMessage(HttpMethod.Post, packageService) { Content = content };
        AddBearerIfPresent(request, token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AASX File Server returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("packageId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException("AASX File Server did not return a packageId.");
        var packageId = id.GetString()!;
        if (packageId.Length > 256 || !packageId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new InvalidDataException("AASX File Server returned an unsafe or non-encoded packageId.");
        return packageId;
    }

    private async Task RollbackRecordsAsync(IReadOnlyCollection<string> descriptorIds, IReadOnlyCollection<string> shellIds, IReadOnlyCollection<string> submodelIds, IReadOnlyCollection<string> conceptIds, CancellationToken ct)
    {
        foreach (var id in descriptorIds.Reverse()) await DeleteBestEffortAsync(RegistryUri($"shell-descriptors/{EncodeId(id)}"), ct);
        foreach (var id in shellIds.Reverse()) await DeleteBestEffortAsync(RepositoryUri($"shells/{EncodeId(id)}"), ct);
        foreach (var id in submodelIds.Reverse()) await DeleteBestEffortAsync(RepositoryUri($"submodels/{EncodeId(id)}"), ct);
        foreach (var id in conceptIds.Reverse()) await DeleteBestEffortAsync(RepositoryUri($"concept-descriptions/{EncodeId(id)}"), ct);
    }

    private async Task DeleteBestEffortAsync(Uri uri, CancellationToken ct)
    {
        try { await DeleteIfPresentAsync(uri, ct); }
        catch (Exception error) { logger.LogError(error, "AASX rollback could not remove {ResourcePath}.", uri.AbsolutePath); }
    }

    private async Task DeletePackageIfPresentAsync(string packageId, CancellationToken ct)
    {
        if (packageId.Length > 256 || !packageId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new InvalidDataException("AASX package receipt contains an invalid encoded packageId.");
        var uri = JoinBase(ConfiguredUri("AASX_FILE_SERVER_URL"), $"packages/{packageId}");
        await DeleteBestEffortAsync(uri, ct);
    }
}
