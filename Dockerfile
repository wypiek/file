# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FileHost.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown $APP_UID:$APP_UID /data
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=6968 Storage__Path=/data DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 6968
ENTRYPOINT ["dotnet", "FileHost.dll"]
