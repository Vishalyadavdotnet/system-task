FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SRAAS.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish SRAAS.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# safer user creation
RUN useradd -m appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SRAAS.Api.dll"]