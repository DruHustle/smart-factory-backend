# Optional local service image. Production uses the dashboard Render bundle.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /source
COPY src/BuildingBlocks/ ./src/BuildingBlocks/
COPY src/Services/NotificationService/ ./src/Services/NotificationService/
RUN dotnet publish src/Services/NotificationService/NotificationService.csproj -c Release -o /published /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=app:app /published ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s CMD curl -f -s http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "NotificationService.dll"]
