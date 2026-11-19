# مرحلة البناء
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
# حددنا المشروع بالاسم عشان ما يضيع
RUN dotnet restore ChatPlatform.csproj
RUN dotnet publish ChatPlatform.csproj -c Release --no-restore -o /app/publish

# مرحلة التشغيل
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# هذا السطر هو اللي يحفظ القاعدة، لا تحذفه
RUN mkdir -p /data && chmod 755 /data
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV CHATPLATFORM_DATA_DIR=/data
ENTRYPOINT ["dotnet", "ChatPlatform.dll"]
