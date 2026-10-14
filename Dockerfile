FROM mcr.microsoft.com/dotnet/aspnet:10.0.0-alpine3.22 AS base
WORKDIR /app
EXPOSE 8080
RUN apk add --no-cache curl
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0.100-alpine3.22 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["EagleTunnelApi/EagleTunnelApi.csproj", "EagleTunnelApi/"]
COPY ["EagleTunnelApi.ServiceDefaults/EagleTunnelApi.ServiceDefaults.csproj", "EagleTunnelApi.ServiceDefaults/"]
RUN dotnet restore "EagleTunnelApi/EagleTunnelApi.csproj"
COPY . .
WORKDIR "/src/EagleTunnelApi"
RUN dotnet build "./EagleTunnelApi.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./EagleTunnelApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -f http://localhost:8080/alive || exit 1
ENTRYPOINT ["dotnet", "EagleTunnelApi.dll"]
