# ========================================
# Smart Factory IoT Backend - DeviceService Dockerfile
# ========================================
# This Dockerfile follows best practices for .NET microservices:
# - Multi-stage build for optimized image size
# - Non-root user for enhanced security
# - Health check endpoint for container orchestration
# - Build-time optimizations for faster CI/CD
# ========================================

# ----------------------------------------
# Stage 1: Base Runtime Image
# ----------------------------------------
# Uses the ASP.NET Core runtime image (smaller than SDK)
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS base
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 PORT=8080
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Expose HTTP and HTTPS ports
EXPOSE 8080
EXPOSE 443

# Create a non-root user for running the application (Security Best Practice)
RUN adduser --disabled-password --gecos "" appuser

# ----------------------------------------
# Stage 2: Build Stage
# ----------------------------------------
# Uses the full SDK image for building the application
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /src

# Copy solution and project files first (Layer Caching Optimization)
# This allows Docker to cache the restore layer if dependencies haven't changed
COPY ["src/SmartFactory.sln", "src/"]
COPY ["src/BuildingBlocks/DashboardAccess/DashboardAccess.csproj", "src/BuildingBlocks/DashboardAccess/"]
COPY ["src/Services/DeviceService/DeviceService.csproj", "src/Services/DeviceService/"]

# Restore NuGet packages (Dependency Inversion Principle - depends on abstractions)
RUN dotnet restore "src/Services/DeviceService/DeviceService.csproj"

# Copy the entire source code
COPY . .

# Build the application in Release configuration
WORKDIR "/src/src/Services/DeviceService"
RUN dotnet build "DeviceService.csproj" -c Release -o /app/build

# ----------------------------------------
# Stage 3: Publish Stage
# ----------------------------------------
# Publishes the application with optimizations
FROM build AS publish
RUN dotnet publish "DeviceService.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ----------------------------------------
# Stage 4: Final Runtime Image
# ----------------------------------------
# Creates the final lightweight image
FROM base AS final
WORKDIR /app

# Copy published application from publish stage
COPY --from=publish /app/publish .

# Set ownership to non-root user
RUN chown -R appuser:appuser /app

# Switch to non-root user (Security Best Practice)
USER appuser

# Configure health check for Kubernetes liveness/readiness probes
# Assumes the service exposes a /health endpoint (Single Responsibility Principle)
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health || exit 1

# Set the entry point for the container
ENTRYPOINT ["dotnet", "DeviceService.dll"]
