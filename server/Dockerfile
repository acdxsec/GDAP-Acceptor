FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY shared/ shared/
COPY server/ server/
RUN dotnet restore server/GdapStatusServer.csproj --locked-mode
RUN dotnet publish server/GdapStatusServer.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "GdapStatusServer.dll"]
