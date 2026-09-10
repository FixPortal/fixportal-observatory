FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/AiObservatory.Ingest/AiObservatory.Ingest.csproj", "src/AiObservatory.Ingest/"]
COPY ["src/AiObservatory.Data/AiObservatory.Data.csproj", "src/AiObservatory.Data/"]
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["eng/analysis/CodeStyle.globalconfig", "eng/analysis/"]
COPY ["nuget.config", "."]
RUN dotnet restore "src/AiObservatory.Ingest/AiObservatory.Ingest.csproj"
COPY . .
RUN dotnet publish "src/AiObservatory.Ingest/AiObservatory.Ingest.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AiObservatory.Ingest.dll"]
