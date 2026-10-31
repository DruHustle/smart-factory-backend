extern alias DeviceRuntime;
using DeviceServiceProgram = DeviceRuntime::Program;
using SmartFactory.BuildingBlocks.DashboardAccess;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using SmartFactory.Services.DeviceService.Domain.Entities;
using SmartFactory.Services.DeviceService.Domain.Interfaces;

namespace SmartFactory.Tests
{
    public class DeviceServiceAuthorizationTests : IClassFixture<WebApplicationFactory<DeviceServiceProgram>>
    {
        private const string Secret = "device-service-e2e-test-secret-at-least-32-bytes";
        private const string ProvisioningToken = "connectivity-fixture-service-token-at-least-32-bytes";
        private readonly HttpClient _client;

        public DeviceServiceAuthorizationTests(WebApplicationFactory<DeviceServiceProgram> factory)
        {
            _client = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("JWT_SECRET", Secret);
                builder.UseSetting("AAS_PROVISIONING_TOKEN", ProvisioningToken);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDeviceRepository>();
                    services.RemoveAll<IDashboardSessionValidator>();
                    services.AddSingleton<IDashboardSessionValidator>(new FixtureSessionValidator());
                    services.AddSingleton<IDeviceRepository>(new FakeDeviceRepository());
                    services.AddSingleton<IStartupFilter, StreamResponseBodyStartupFilter>();
                });
            }).CreateClient();
        }

        [Fact]
        public async Task DeviceRoutesRejectRequestsWithoutDashboardJwt()
        {
            var response = await _client.GetAsync("/api/devices");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData("sync")]
        [InlineData("probe")]
        public async Task ConnectivityRoutesRequireThePrivateProvisioningToken(string operation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assets/connectivity/{operation}")
            {
                Content = JsonContent.Create(new { }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer"));
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData("sync")]
        [InlineData("probe")]
        public async Task ConnectivityRoutesValidateBeforeAnyBrokerPublication(string operation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assets/connectivity/{operation}")
            {
                Content = JsonContent.Create(new { execute = "unsupported" }),
            };
            request.Headers.Add("X-AAS-Provisioning-Token", ProvisioningToken);
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("unsupported", await response.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("viewer")]
        [InlineData("operator")]
        [InlineData("engineer")]
        [InlineData("admin")]
        public async Task ReadRoutesAcceptAuthenticatedDashboardRoles(string role)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role));

            var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task DeviceRegistrationRequiresEngineerRole()
        {
            using var viewerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices")
            {
                Content = JsonContent.Create(new { deviceId = "viewer-device", name = "Viewer Device", type = "gateway", status = "online" }),
            };
            viewerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("viewer"));
            var forbidden = await _client.SendAsync(viewerRequest);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var engineerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices")
            {
                Content = JsonContent.Create(new { deviceId = "engineer-device", name = "Engineer Device", type = "gateway", status = "online" }),
            };
            engineerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer"));
            var created = await _client.SendAsync(engineerRequest);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Ada031MotionRouteRejectsViewerRoleBeforePublishing()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/control")
            {
                Content = JsonContent.Create(new
                {
                    schemaVersion = 1,
                    gatewayDeviceId = "pi-edge-01",
                    assetId = "urn:test:arm",
                    joint = "base",
                    direction = "increase",
                    commandId = "command-0001",
                    expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5000,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("viewer"));

            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task WroverIndicatorRouteRejectsViewerRoleBeforePublishing()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/gpio-control")
            {
                Content = JsonContent.Create(new
                {
                    schemaVersion = 1,
                    gatewayDeviceId = "pi-edge-01",
                    targetDeviceId = "esp32-wrover-01",
                    pin = 18,
                    value = 1,
                    holdMs = 2000,
                    commandId = "indicator-0001",
                    expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5000,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("viewer"));

            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task WroverIndicatorRouteAllowsOperatorRoleToReachPublisher()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/gpio-control")
            {
                Content = JsonContent.Create(new
                {
                    schemaVersion = 1,
                    gatewayDeviceId = "pi-edge-01",
                    targetDeviceId = "esp32-wrover-01",
                    pin = 18,
                    value = 1,
                    holdMs = 2000,
                    commandId = "indicator-operator-0001",
                    expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5000,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("operator"));

            var response = await _client.SendAsync(request);

            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task FirmwareUpdateRoutesFailClosedWithoutClaimingDelivery()
        {
            using var viewerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/1/trigger-update")
            {
                Content = JsonContent.Create(new { targetVersion = "2.0.0" }),
            };
            viewerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("viewer"));
            var forbidden = await _client.SendAsync(viewerRequest);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var triggerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/1/trigger-update")
            {
                Content = JsonContent.Create(new { targetVersion = "2.0.0" }),
            };
            triggerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer"));
            var notImplemented = await _client.SendAsync(triggerRequest);
            Assert.Equal(HttpStatusCode.NotImplemented, notImplemented.StatusCode);
            Assert.Contains("No firmware update was queued", await notImplemented.Content.ReadAsStringAsync());

            using var statusRequest = new HttpRequestMessage(HttpMethod.Post, "/api/devices/1/update-status")
            {
                Content = JsonContent.Create("Completed"),
            };
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer"));
            var statusNotImplemented = await _client.SendAsync(statusRequest);
            Assert.Equal(HttpStatusCode.NotImplemented, statusNotImplemented.StatusCode);
            Assert.Contains("No firmware status was changed", await statusNotImplemented.Content.ReadAsStringAsync());
        }

        private static string CreateToken(string role)
        {
            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "smart-factory-iot",
                audience: "smart-factory-iot-api",
                claims: new[]
                {
                    new Claim(JwtRegisteredClaimNames.Sub, $"test-{role}"),
                    new Claim("openId", $"test-{role}"),
                    new Claim("name", $"Test {role}"),
                    new Claim("role", role),
                    new Claim("sid", "11000000-0000-4000-8000-000000000001"),
                    new Claim("sv", "0"),
                },
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // The role/controller suite isolates publishers from storage. A separate
        // PostgreSQL regression qualifies the real revocation implementation.
        private sealed class FixtureSessionValidator : IDashboardSessionValidator {
            public Task<bool> Current(string openId, string sid, int version, string? role, CancellationToken ct) => Task.FromResult(openId == "test-" + role && version == 0 && Guid.TryParse(sid, out _));
        }
        private sealed class FakeDeviceRepository : IDeviceRepository
        {
            private readonly Dictionary<int, Device> _devices = new();
            private int _nextId;

            public Task<IEnumerable<Device>> GetAllAsync() => Task.FromResult<IEnumerable<Device>>(_devices.Values.ToList());

            public Task<Device?> GetByIdAsync(int id) => Task.FromResult(_devices.TryGetValue(id, out var device) ? device : null);

            public Task AddAsync(Device device)
            {
                device.Id = Interlocked.Increment(ref _nextId);
                _devices[device.Id] = device;
                return Task.CompletedTask;
            }

            public Task UpdateAsync(Device device)
            {
                _devices[device.Id] = device;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(int id)
            {
                _devices.Remove(id);
                return Task.CompletedTask;
            }
        }

        private sealed class StreamResponseBodyStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Features.Set<IHttpResponseBodyFeature>(
                        new StreamResponseBodyFeature(context.Response.Body));
                    await nextMiddleware();
                });
                next(app);
            };
        }
    }
}
