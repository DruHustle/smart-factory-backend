# Use the SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /src

# Copy the solution file and restore all projects
COPY ["src/SmartFactory.sln", "src/"]
COPY ["src/Services/AnalyticsService/AnalyticsService.csproj", "src/Services/AnalyticsService/"]
COPY ["src/Services/DeviceService/DeviceService.csproj", "src/Services/DeviceService/"]
COPY ["src/Services/IdentityService/IdentityService.csproj", "src/Services/IdentityService/"]
COPY ["src/Services/NotificationService/NotificationService.csproj", "src/Services/NotificationService/"]
COPY ["src/Services/TelemetryService/TelemetryService.csproj", "src/Services/TelemetryService/"]
COPY ["src/BuildingBlocks/DashboardAccess/DashboardAccess.csproj", "src/BuildingBlocks/DashboardAccess/"]
COPY ["src/SmartFactory.Tests/SmartFactory.Tests.csproj", "src/SmartFactory.Tests/"]

RUN dotnet restore "src/SmartFactory.sln"

# Copy the entire source code
COPY . .

# Build and publish all services so runtime can select one via env var.
RUN dotnet publish "src/Services/DeviceService/DeviceService.csproj" -c Release -o /app/publish/DeviceService && \
    dotnet publish "src/Services/IdentityService/IdentityService.csproj" -c Release -o /app/publish/IdentityService && \
    dotnet publish "src/Services/NotificationService/NotificationService.csproj" -c Release -o /app/publish/NotificationService && \
    dotnet publish "src/Services/AnalyticsService/AnalyticsService.csproj" -c Release -p:OutputType=Exe -o /app/publish/AnalyticsService && \
    dotnet publish "src/Services/TelemetryService/TelemetryService.csproj" -c Release -o /app/publish/TelemetryService && \
    test -f /app/publish/DeviceService/DeviceService.runtimeconfig.json && \
    test -f /app/publish/IdentityService/IdentityService.runtimeconfig.json && \
    test -f /app/publish/NotificationService/NotificationService.runtimeconfig.json && \
    test -f /app/publish/AnalyticsService/AnalyticsService.runtimeconfig.json && \
    test -f /app/publish/TelemetryService/TelemetryService.runtimeconfig.json

# Use the runtime image for the final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS final
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
COPY --from=build --chown=app:app /src/deploy/migrations ./deploy/migrations
COPY --chown=app:app deploy/run-service.sh /app/run-service
RUN chmod 0555 /app/run-service

# Select the service at runtime using SERVICE_NAME.
# Valid values: DeviceService, IdentityService, NotificationService, AnalyticsService, TelemetryService
ENV SERVICE_NAME=DeviceService
USER app
ENTRYPOINT ["/app/run-service"]
