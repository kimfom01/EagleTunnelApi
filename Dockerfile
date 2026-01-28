FROM mcr.microsoft.com/dotnet/aspnet:10.0.0-alpine3.22 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0.100-alpine3.22 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["EagleTunnelApi/EagleTunnelApi.csproj", "EagleTunnelApi/"]
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
ENTRYPOINT ["dotnet", "EagleTunnelApi.dll"]
