FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TelegramManager.Bot/TelegramManager.Bot.csproj src/TelegramManager.Bot/
RUN dotnet restore src/TelegramManager.Bot/TelegramManager.Bot.csproj
COPY src/TelegramManager.Bot/ src/TelegramManager.Bot/
RUN dotnet publish src/TelegramManager.Bot/TelegramManager.Bot.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TelegramManager.Bot.dll"]
