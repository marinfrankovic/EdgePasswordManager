# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY EdgePasswordBulkManager.sln ./
COPY src/EdgePasswordBulkManager/EdgePasswordBulkManager.csproj src/EdgePasswordBulkManager/
RUN dotnet restore src/EdgePasswordBulkManager/EdgePasswordBulkManager.csproj

COPY src/ src/
RUN dotnet publish src/EdgePasswordBulkManager/EdgePasswordBulkManager.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish ./
EXPOSE 8080

ENTRYPOINT ["dotnet", "EdgePasswordBulkManager.dll"]
