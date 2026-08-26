FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/TradingBot.Worker/TradingBot.Worker.csproj", "src/TradingBot.Worker/"]
RUN dotnet restore "src/TradingBot.Worker/TradingBot.Worker.csproj"
COPY . .
RUN dotnet publish "src/TradingBot.Worker/TradingBot.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TradingBot.Worker.dll"]
