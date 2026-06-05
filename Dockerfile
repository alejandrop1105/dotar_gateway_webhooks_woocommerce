FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER app
WORKDIR /app
EXPOSE 5200

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Dotar.Gateway/Dotar.Gateway.csproj", "src/Dotar.Gateway/"]
RUN dotnet restore "./src/Dotar.Gateway/Dotar.Gateway.csproj"
COPY . .
WORKDIR "/src/src/Dotar.Gateway"
RUN dotnet build "./Dotar.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Dotar.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Historial de versiones: queda junto al ejecutable (AppContext.BaseDirectory = /app)
# para que DeployHistoryService lo lea en producción.
COPY version.json ./version.json

# Para SQLite necesitamos asegurarnos de que el usuario 'app' tenga permisos sobre la carpeta de datos.
# Creamos la carpeta /app/data y le damos permisos a app (usuario ID 1654 en la imagen dotnet 8/9).
USER root
RUN mkdir -p /app/data && chown -R app:app /app/data
USER app

ENTRYPOINT ["dotnet", "Dotar.Gateway.dll"]
