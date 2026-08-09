# Build context is the repository root, so EntraGuard.Shared is reachable.
#   docker build -f src/EntraGuard.MediaService/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/EntraGuard.Shared/EntraGuard.Shared.csproj src/EntraGuard.Shared/
COPY src/EntraGuard.MediaService/EntraGuard.MediaService.csproj src/EntraGuard.MediaService/
RUN dotnet restore src/EntraGuard.MediaService/EntraGuard.MediaService.csproj

COPY src/ src/
RUN dotnet publish src/EntraGuard.MediaService/EntraGuard.MediaService.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# The Speech SDK is a native library and needs these at runtime; the aspnet image
# does not carry them. Without libasound2 the recogniser fails at construction with
# an error that does not mention audio at all.
RUN apt-get update \
    && apt-get install --no-install-recommends -y libasound2 libssl3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Container Apps ingress targets 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Non-root. Nothing here needs privilege.
RUN useradd --uid 1001 --create-home entraguard
USER 1001

ENTRYPOINT ["dotnet", "EntraGuard.MediaService.dll"]
