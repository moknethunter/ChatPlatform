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

# ضروري للسماح بالكتابة إلى Fly Volume
USER root

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV CHATPLATFORM_DATA_DIR=/data

EXPOSE 8080

ENTRYPOINT ["dotnet", "ChatPlatform.dll"]
