# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/RadioApp.Server/RadioApp.Server.csproj", "src/RadioApp.Server/"]
COPY ["src/RadioApp.Core/RadioApp.Core.csproj", "src/RadioApp.Core/"]
RUN dotnet restore "src/RadioApp.Server/RadioApp.Server.csproj"

COPY src/RadioApp.Server/ src/RadioApp.Server/
COPY src/RadioApp.Core/ src/RadioApp.Core/
WORKDIR /src/src/RadioApp.Server
RUN dotnet publish "RadioApp.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:5480
ENV RadioServer__StationsJsonPath=/data/stations.json
ENV RadioServer__MusicRootPath=/music
ENV RadioServer__FfmpegPath=ffmpeg

EXPOSE 5480

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD curl -f http://127.0.0.1:5480/api/health || exit 1

ENTRYPOINT ["dotnet", "RadioApp.Server.dll"]
