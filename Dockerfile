FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MyRegistry.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore MyRegistry.sln
RUN dotnet publish src/MyRegistry.Web -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# git : requis par LocalGitProvider (snapshot de la marketplace vers un dépôt local)
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Utilisateur non-root ; seule écriture autorisée : /data (jetable)
RUN mkdir -p /data && chown app:app /data
USER app
ENTRYPOINT ["dotnet", "MyRegistry.Web.dll"]
