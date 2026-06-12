# Build from repo root: docker build -f src/StreamDeckPilot.Api/Dockerfile -t stream-deck-pilot .
# Multi-arch: the build stage stays on the native BUILDPLATFORM and cross-publishes for the
# requested TARGETARCH, so only the small final runtime image differs per architecture.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
# Map Docker's TARGETARCH (amd64/arm64) to the .NET RID arch (x64/arm64).
RUN case "$TARGETARCH" in \
        amd64) DOTNET_ARCH=x64 ;; \
        arm64) DOTNET_ARCH=arm64 ;; \
        *) DOTNET_ARCH="$TARGETARCH" ;; \
    esac \
    && dotnet publish src/StreamDeckPilot.Api -c Release -a "$DOTNET_ARCH" --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libhidapi-libusb0 libusb-1.0-0 \
        fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "StreamDeckPilot.Api.dll"]
