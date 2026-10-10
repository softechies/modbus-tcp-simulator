FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ModbusTcpSimulator -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
COPY samples ./samples
EXPOSE 502
ENTRYPOINT ["./modbus-sim"]
CMD ["--config", "samples/tank-farm.json", "--port", "502"]
