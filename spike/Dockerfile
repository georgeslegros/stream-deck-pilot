# Stage 1 — build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY StreamDeckSpike/ .
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app

# Stage 2 — runtime (includes libhidapi for USB HID access)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends libhidapi-libusb0 libusb-1.0-0 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "StreamDeckSpike.dll"]
