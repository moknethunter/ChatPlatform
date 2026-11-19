==========================================

BUILD STAGE

==========================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY . .

RUN dotnet restore ChatPlatform.csproj

RUN dotnet publish ChatPlatform.csproj 
-c Release 
--no-restore 
-o /app/publish

==========================================

RUNTIME STAGE

==========================================

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

تجهيز مجلد قاعدة البيانات

السماح لمستخدم التطبيق بالكتابة

RUN mkdir -p /data 
&& chmod 777 /data

إعدادات التطبيق

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV CHATPLATFORM_DATA_DIR=/data

تشغيل التطبيق

ENTRYPOINT ["dotnet", "ChatPlatform.dll"]
