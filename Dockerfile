FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/TradingBot.Worker/TradingBot.Worker.csproj", "src/TradingBot.Worker/"]
RUN dotnet restore "src/TradingBot.Worker/TradingBot.Worker.csproj"
COPY . .
RUN dotnet publish "src/TradingBot.Worker/TradingBot.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENTRYPOINT ["dotnet", "TradingBot.Worker.dll"]
