using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.Services.DeviceService.Application.DTOs;
using SmartFactory.Services.DeviceService.Application.Services;

namespace SmartFactory.Services.DeviceService.API.Controllers;

[ApiController]
[Route("api/assets")]
public sealed class AssetsController(AasProvisioningService provisioner, EdgeConfigurationPublisher edgePublisher, RedisAasChangePublisher changes, IConfiguration configuration, ILogger<AssetsController> logger) : ControllerBase
{
    private const long MaxAasxBytes = 50L * 1024 * 1024;

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> Create([FromBody] AssetProvisionRequest asset, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try
        {
            var result = await provisioner.CreateAsync(asset, ct);
            await changes.PublishAsync(asset.AssetId, "created", asset.AasVersion, ct);
            return Ok(result);
        }
        catch (AasProvisioningConfigurationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "AAS asset provisioning failed.");
            return MapProvisioningFailure(error) ?? StatusCode(502, new { error = "AAS repository or registry provisioning failed." });
        }
    }

    [HttpPut, AllowAnonymous]
    public async Task<IActionResult> Update([FromBody] AssetProvisionRequest asset, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try
        {
            var result = await provisioner.UpdateAsync(asset, ct);
            await changes.PublishAsync(asset.AssetId, "updated", asset.AasVersion, ct);
            return Ok(result);
        }
        catch (AasProvisioningConfigurationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (AasVersionConflictException error) { return Conflict(new { error = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "AAS asset update failed.");
            return MapProvisioningFailure(error) ?? StatusCode(502, new { error = "AAS repository or registry update failed." });
        }
    }

    [HttpDelete, AllowAnonymous]
    public async Task<IActionResult> Delete([FromBody] AssetProvisionRequest asset, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try
        {
            var result = await provisioner.DeleteAsync(asset, ct);
            await changes.PublishAsync(asset.AssetId, "deleted", asset.AasVersion, ct);
            return Ok(result);
        }
        catch (AasProvisioningConfigurationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "AAS asset compensation failed.");
            return MapProvisioningFailure(error) ?? StatusCode(502, new { error = "AAS repository or registry cleanup failed." });
        }
    }

    [HttpPost("import"), AllowAnonymous, RequestSizeLimit(MaxAasxBytes + 64 * 1024), RequestFormLimits(MultipartBodyLengthLimit = MaxAasxBytes + 64 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Import([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        if (file is null || file.Length is <= 0 or > MaxAasxBytes || !Path.GetExtension(file.FileName).Equals(".aasx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Upload one .aasx file no larger than 50 MB." });
        if (!string.IsNullOrWhiteSpace(file.ContentType) && file.ContentType is not ("application/aas+zip" or "application/octet-stream"))
            return BadRequest(new { error = "The uploaded file must use the AASX ZIP media type." });

        try
        {
            await using var input = file.OpenReadStream();
            await using var buffer = new MemoryStream(checked((int)file.Length));
            await input.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxAasxBytes) return BadRequest(new { error = "AASX package exceeds the 50 MB limit." });
            var bytes = buffer.ToArray();
            if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K') return BadRequest(new { error = "The package is not a ZIP-based AASX file." });
            var result = await provisioner.ImportAasxAsync(bytes, Path.GetFileName(file.FileName), ct);
            if (result.GetValueOrDefault("assetAdministrationShells") is JsonArray shells)
            {
                foreach (var shell in shells.OfType<JsonObject>())
                {
                    var id = shell["id"]?.GetValue<string>();
                    var versionText = shell["administration"]?["version"]?.GetValue<string>();
                    int? version = int.TryParse(versionText, out var parsedVersion) ? parsedVersion : null;
                    if (!string.IsNullOrWhiteSpace(id)) await changes.PublishAsync(id, "imported", version, ct);
                }
            }
            return Ok(result);
        }
        catch (InvalidDataException error) { return BadRequest(new { error = error.Message }); }
        catch (AasProvisioningConfigurationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "AASX import failed.");
            return MapProvisioningFailure(error) ?? StatusCode(502, new { error = "AASX validation or repository import failed." });
        }
    }

    [HttpDelete("import"), AllowAnonymous]
    public async Task<IActionResult> RollbackImport([FromBody] AasxImportReceipt receipt, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try { await provisioner.RollbackAasxAsync(receipt, ct); return Ok(new { rolledBack = true }); }
        catch (Exception error) { logger.LogError(error, "AASX compensating cleanup failed."); return StatusCode(502, new { error = "AASX compensating cleanup failed." }); }
    }

    [HttpPost("sync"), AllowAnonymous]
    public async Task<IActionResult> Sync([FromBody] EdgeConfigurationRequest request, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try { await edgePublisher.PublishAsync(request, ct); return Ok(new { published = true }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (Exception error) { logger.LogError(error, "Edge configuration publish failed."); return StatusCode(502, new { error = "MQTT configuration publication failed." }); }
    }

    [HttpPost("connectivity/sync"), AllowAnonymous, RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> SyncConnectivity([FromBody] JsonElement request, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try
        {
            await edgePublisher.PublishConnectivityAsync(request, ct);
            return Accepted(new { published = true, applied = false, configurationRevision = decimal.ToInt64(request.GetProperty("revision").GetDecimal()) });
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException) { return StatusCode(503, new { error = "Connectivity publication is not configured." }); }
        catch (Exception error) { logger.LogError(error, "Connectivity profile publication failed."); return StatusCode(502, new { error = "Connectivity profile could not be published." }); }
    }

    [HttpPost("connectivity/probe"), AllowAnonymous, RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> ProbeConnectivity([FromBody] JsonElement request, CancellationToken ct)
    {
        if (!HasProvisioningToken()) return Unauthorized(new { error = "A valid provisioning service token is required." });
        try
        {
            await edgePublisher.ProbeConnectivityAsync(request, ct);
            return Accepted(new { published = true, commandId = request.GetProperty("commandId").GetString(), deviceConnectionConfirmed = false });
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException) { return StatusCode(503, new { error = "Connectivity publication is not configured." }); }
        catch (Exception error) { logger.LogError(error, "Connectivity probe publication failed."); return StatusCode(502, new { error = "Connectivity probe could not be published." }); }
    }

    [HttpPost("control"), Authorize(Roles = "engineer,admin")]
    public async Task<IActionResult> ControlAda031([FromBody] Ada031ControlRequest request, CancellationToken ct)
    {
        try
        {
            await edgePublisher.PublishAda031ControlAsync(request, ct);
            return Accepted(new { published = true, commandId = request.CommandId, physicalMotionConfirmed = false });
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "ADA031 control command publish failed.");
            return StatusCode(502, new { error = "ADA031 command could not be published to the gateway." });
        }
    }

    [HttpPost("gpio-control"), Authorize(Roles = "operator,engineer,admin")]
    public async Task<IActionResult> ControlWroverIndicator([FromBody] GpioControlRequest request, CancellationToken ct)
    {
        try
        {
            await edgePublisher.PublishGpioControlAsync(request, ct);
            return Accepted(new { published = true, commandId = request.CommandId, physicalStateConfirmed = false });
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return StatusCode(503, new { error = error.Message }); }
        catch (Exception error)
        {
            logger.LogError(error, "WROVER indicator command publish failed.");
            return StatusCode(502, new { error = "WROVER indicator command could not be published to the gateway." });
        }
    }

    private IActionResult? MapProvisioningFailure(Exception error)
    {
        var causes = new List<Exception>();
        for (Exception? current = error; current is not null && causes.Count < 8; current = current.InnerException) causes.Add(current);
        if (causes.OfType<AasProvisioningConfigurationException>().FirstOrDefault() is { } configurationError)
            return StatusCode(503, new { error = configurationError.Message });
        if (causes.OfType<AasVersionConflictException>().Any())
            return Conflict(new { error = "The AAS resource already exists or changed since it was read." });
        if (causes.OfType<ArgumentException>().Any())
            return BadRequest(new { error = "The AAS provisioning request contains invalid asset data." });
        var upstreamStatus = causes.OfType<HttpRequestException>().Select(exception => exception.StatusCode).FirstOrDefault(status => status.HasValue);
        if (upstreamStatus is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            return Conflict(new { error = "The AAS repository rejected a conflicting resource revision." });
        if (upstreamStatus is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            return UnprocessableEntity(new { error = "The AAS service rejected the model payload." });
        return null;
    }

    private bool HasProvisioningToken()
    {
        var expected = configuration["AAS_PROVISIONING_TOKEN"];
        if (string.IsNullOrWhiteSpace(expected) || Encoding.UTF8.GetByteCount(expected) < 32) return false;
        var provided = Request.Headers["X-AAS-Provisioning-Token"].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
    }
}

public sealed record AasxImportReceipt(string PackageId, string[] AssetIds, string[] SubmodelIds, string[] ConceptDescriptionIds);
