FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY . .

RUN dotnet restore ChatPlatform.csproj

RUN dotnet publish ChatPlatform.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ARG APP_REVISION=unknown
ENV CHATPLATFORM_REVISION=${APP_REVISION}

WORKDIR /app

COPY --from=build /app/publish .

# مهم جدًا:
# يسمح للتطبيق بالكتابة إلى Fly Volume وDocker volume
USER root

RUN mkdir -p /app/data \
    && chmod 777 /app/data

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV CHATPLATFORM_DATA_DIR=/app/data
ENV CHATPLATFORM_ALLOW_EPHEMERAL_DB=false
ENV DOTNET_EnableDiagnostics=0

EXPOSE 8080

ENTRYPOINT ["dotnet", "ChatPlatform.dll"]
