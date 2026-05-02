FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/OrderService.Contracts/OrderService.Contracts.csproj src/OrderService.Contracts/
COPY src/OrderService.Api/OrderService.Api.csproj src/OrderService.Api/
RUN dotnet restore src/OrderService.Api/OrderService.Api.csproj

COPY src/ src/
RUN dotnet publish src/OrderService.Api -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
COPY --from=build --chown=appuser:appuser /app/publish .
USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "OrderService.Api.dll"]
