using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.Services.DeviceService.Application.DTOs;
using SmartFactory.Services.DeviceService.Application.Services;

namespace SmartFactory.Tests;

public sealed class AasProvisioningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledCreateOrImportRemovesConfirmedWrites(bool import)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new RevisionHandler();
        var handler = new CancelWriteHandler(repository, HttpMethod.Post, cancellation);
        var service = CreateService(handler);
        if (import)
        {
            var documents = AasDocumentsBuilder.Build(Asset());
            var model = JsonSerializer.Serialize(new { assetAdministrationShells = new[] { documents.Shell }, submodels = documents.Submodels });
            using var bytes = new MemoryStream();
            using (var package = new AasCore.Aas3.Package.Packaging().Create(bytes))
            {
                var part = package.PutPart(new Uri("/aasx/environment.json", UriKind.Relative), "application/json", Encoding.UTF8.GetBytes(model));
                package.MakeSpec(part);
                package.Flush();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAasxAsync(bytes.ToArray(), "cancelled.aasx", cancellation.Token));
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(Asset(), cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(repository.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Empty(repository.Resources);
    }

    [Fact]
    public async Task CancelledRevisionRestoresConfirmedWrites()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new RevisionHandler();
        var service = CreateService(new CancelWriteHandler(repository, HttpMethod.Put, cancellation));
        await service.CreateAsync(Asset(), CancellationToken.None);
        var before = repository.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);
        var revision = Asset() with { AasVersion = 2, ExpectedAasVersion = 1, ManufacturerStreet = "Changed street" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateAsync(revision, cancellation.Token));
        Assert.Equal(2, repository.Requests.Count(request => request.Method == HttpMethod.Put));
        Assert.Equal(before.OrderBy(pair => pair.Key), repository.Resources.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task CreatesTemplateDocumentsAndRegistersRepositoryAndRegistryRecords()
    {
        var handler = new CaptureHandler();
        var service = CreateService(handler);
        var result = await service.CreateAsync(Asset(), CancellationToken.None);
        Assert.Contains("shell", result.Keys);
        Assert.Contains("submodels", result.Keys);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path.EndsWith("/shells", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path.EndsWith("/shell-descriptors", StringComparison.Ordinal));
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("test-aas-token", request.BearerToken));
        using var nameplate = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("Nameplate", nameplate.RootElement.GetProperty("idShort").GetString());
        Assert.Equal("https://admin-shell.io/idta-02006-3-0", nameplate.RootElement.GetProperty("administration").GetProperty("templateId").GetString());
        using var technicalData = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("TechnicalData", technicalData.RootElement.GetProperty("idShort").GetString());
    }

    [Fact]
    public async Task UsesOidcDiscoveryAndConfiguredScopeForClientCredentials()
    {
        var handler = new CaptureHandler();
        var service = CreateService(handler, useDiscovery: true);
        await service.CreateAsync(Asset(), CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/.well-known/openid-configuration");
        var tokenRequest = Assert.Single(handler.Requests, request => request.Path == "/token");
        var tokenFields = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(tokenRequest.Body);
        Assert.Equal("client_credentials", tokenFields["grant_type"]);
        Assert.Equal("aas.read aas.write", tokenFields["scope"]);
        Assert.All(handler.Requests.Where(request => request.Method != HttpMethod.Get && request.Path != "/token"), request => Assert.Equal("test-aas-token", request.BearerToken));
    }

    [Fact]
    public async Task AllowsBundledUnauthenticatedBasyxServicesInProductionWhenExplicitlyEnabled()
    {
        var handler = new CaptureHandler();
        var settings = BundledBasyxSettings();
        settings["AAS_ALLOW_UNAUTHENTICATED_PRIVATE"] = "true";
        var service = CreateService(handler, settings, Environments.Production);

        await service.CreateAsync(Asset(), CancellationToken.None);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Null(request.BearerToken));
    }

    [Fact]
    public async Task RejectsUnauthenticatedRemoteAasEvenWhenPrivateOverrideIsEnabled()
    {
        var handler = new CaptureHandler();
        var settings = BundledBasyxSettings();
        settings["AAS_REPOSITORY_URL"] = "http://public-aas.example";
        settings["AAS_ALLOW_UNAUTHENTICATED_PRIVATE"] = "true";
        var service = CreateService(handler, settings, Environments.Production);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Asset(), CancellationToken.None));
        Assert.IsType<AasProvisioningConfigurationException>(error.InnerException);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdatesExpectedRevisionAndRejectsAStaleEditor()
    {
        var handler = new RevisionHandler();
        var service = CreateService(handler);
        await service.CreateAsync(Asset(), CancellationToken.None);

        var revisionTwo = Asset() with { AasVersion = 2, ExpectedAasVersion = 1, ManufacturerStreet = "Updated Street 2" };
        await service.UpdateAsync(revisionTwo, CancellationToken.None);
        var writesAfterUpdate = handler.Requests.Count(request => request.Method == HttpMethod.Put);
        Assert.Equal(5, writesAfterUpdate);

        foreach (var request in handler.Requests.Where(request => request.Method == HttpMethod.Put && request.Body.Contains("administration", StringComparison.Ordinal)))
        {
            using var model = JsonDocument.Parse(request.Body);
            Assert.Equal("2", model.RootElement.GetProperty("administration").GetProperty("version").GetString());
        }

        await Assert.ThrowsAsync<AasVersionConflictException>(() => service.UpdateAsync(revisionTwo, CancellationToken.None));
        Assert.Equal(writesAfterUpdate, handler.Requests.Count(request => request.Method == HttpMethod.Put));
    }

    [Fact]
    public void RejectsMissingRequiredManufacturerAddress()
    {
        Assert.Throws<ArgumentException>(() => AasDocumentsBuilder.Build(Asset() with { ManufacturerStreet = "" }));
    }

    private static AasProvisioningService CreateService(HttpMessageHandler handler, bool useDiscovery = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AAS_REPOSITORY_URL"] = "https://aas.example", ["AAS_SUBMODEL_REPOSITORY_URL"] = "https://aas.example",
            ["AAS_CONCEPT_DESCRIPTION_REPOSITORY_URL"] = "https://aas.example", ["AAS_REGISTRY_URL"] = "https://aas.example/registry",
            ["AAS_OIDC_TOKEN_URL"] = useDiscovery ? null : "https://identity.example/token",
            ["AAS_OIDC_DISCOVERY_URL"] = useDiscovery ? "https://identity.example/.well-known/openid-configuration" : null,
            ["AAS_OIDC_SCOPE"] = useDiscovery ? "aas.read aas.write" : null,
            ["AAS_OIDC_CLIENT_ID"] = $"test-client-{Guid.NewGuid():N}", ["AAS_OIDC_CLIENT_SECRET"] = "test-secret",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var env = new TestEnvironment { EnvironmentName = Environments.Development };
        return new AasProvisioningService(new HttpClient(handler), config, env, NullLogger<AasProvisioningService>.Instance);
    }

    private static AasProvisioningService CreateService(HttpMessageHandler handler, Dictionary<string, string?> settings, string environment)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var env = new TestEnvironment { EnvironmentName = environment };
        return new AasProvisioningService(new HttpClient(handler), config, env, NullLogger<AasProvisioningService>.Instance);
    }

    private static Dictionary<string, string?> BundledBasyxSettings() => new()
    {
        ["AAS_REPOSITORY_URL"] = "http://aas-repository:8081",
        ["AAS_SUBMODEL_REPOSITORY_URL"] = "http://submodel-repository:8083",
        ["AAS_CONCEPT_DESCRIPTION_REPOSITORY_URL"] = "http://concept-description-repository:8085",
        ["AAS_REGISTRY_URL"] = "http://aas-registry:8082",
        ["AAS_SUBMODEL_REGISTRY_URL"] = "http://submodel-registry:8084",
        ["AASX_FILE_SERVER_URL"] = "http://aasx-file-server:8086",
    };

    private static AssetProvisionRequest Asset() => new()
    {
        AssetId = "urn:test:compressor:01", Name = "Compressor 01", AssetType = "compressor", Manufacturer = "Example Works", Model = "CX-1",
        ManufacturerStreet = "Industrial Road 1", ManufacturerZipcode = "10115", ManufacturerCityTown = "Berlin", ManufacturerNationalCode = "DE",
        ManufacturerArticleNumber = "CX-1-ART", OrderCodeOfManufacturer = "CX-1-ORDER", RatedValue = "75", RatedUnit = "kW",
    };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class RevisionHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> resources = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Resources => resources;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.Method, path, body, request.Headers.Authorization?.Parameter));
            if (path == "/token")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "test-aas-token", expires_in = 300 }), Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get && resources.TryGetValue(path, out var existing))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(existing, Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Delete)
            {
                resources.Remove(path);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Put)
            {
                if (!resources.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                resources[path] = body;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var id = root.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
                var targetPath = path.EndsWith("/shells", StringComparison.Ordinal)
                    ? path + "/" + Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(id!))
                    : path.EndsWith("/submodels", StringComparison.Ordinal)
                        ? path + "/" + Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(id!))
                        : path + "/" + Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(id!));
                resources[targetPath] = body;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class CancelWriteHandler(HttpMessageHandler inner, HttpMethod method, CancellationTokenSource cancellation) : DelegatingHandler(inner)
    {
        private int writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == method && request.RequestUri!.AbsolutePath != "/token" && ++writes == 2)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return base.SendAsync(request, ct);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Body, string? BearerToken);
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.Parameter));
            if (request.RequestUri.AbsolutePath == "/.well-known/openid-configuration")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token_endpoint\":\"https://identity.example/token\"}", Encoding.UTF8, "application/json") };
            if (request.RequestUri.AbsolutePath == "/token")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "test-aas-token", expires_in = 300 }), Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
