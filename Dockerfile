# ============================================================
# Build stage
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY . .

RUN dotnet restore ChatPlatform.csproj

RUN dotnet publish ChatPlatform.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false


# ============================================================
# Runtime stage
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

# Fly.io يركب Volume في /data بعد إنشاء الصورة.
# لذلك يجب أن يكون التطبيق قادرًا على الكتابة إلى هذا المسار.
USER root

RUN mkdir -p /data \
    && chmod 777 /data \
    && mkdir -p /app \
    && chmod 755 /app

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

ENV ASPNETCORE_ENVIRONMENT=Production

ENV CHATPLATFORM_DATA_DIR=/data

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ENTRYPOINT ["dotnet", "ChatPlatform.dll"]
