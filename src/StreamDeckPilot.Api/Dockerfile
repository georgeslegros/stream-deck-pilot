# Build from repo root: docker build -f src/StreamDeckPilot.Api/Dockerfile -t stream-deck-pilot .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/StreamDeckPilot.Api -c Release -r linux-x64 --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libhidapi-libusb0 libusb-1.0-0 \
        fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "StreamDeckPilot.Api.dll"]
