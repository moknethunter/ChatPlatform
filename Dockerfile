# ============================================================
# Build stage
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY . .

RUN dotnet restore ChatPlatform.csproj

RUN dotnet publish ChatPlatform.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false


# ============================================================
# Runtime stage
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

# ============================================================
# Database permissions
# ============================================================

USER root

RUN mkdir -p /data \
    && chmod 777 /data \
    && mkdir -p /app/data \
    && chmod 777 /app/data

# ============================================================
# Environment
# ============================================================

ENV ASPNETCORE_URLS=http://+:8080

ENV ASPNETCORE_ENVIRONMENT=Production

ENV CHATPLATFORM_DATA_DIR=/data

ENV CHATPLATFORM_ALLOW_EPHEMERAL_DB=false

ENV DOTNET_EnableDiagnostics=0

EXPOSE 8080

# ============================================================
# Startup
# ============================================================

ENTRYPOINT ["dotnet", "ChatPlatform.dll"]
