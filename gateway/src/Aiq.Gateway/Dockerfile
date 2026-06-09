# syntax=docker/dockerfile:1.7
ARG DOTNET_VERSION=9.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY src/Aiq.Gateway/Aiq.Gateway.csproj ./Aiq.Gateway/
RUN dotnet restore ./Aiq.Gateway/Aiq.Gateway.csproj

COPY src/Aiq.Gateway/ ./Aiq.Gateway/
RUN dotnet publish ./Aiq.Gateway/Aiq.Gateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true

COPY --from=build /app/publish .

EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "Aiq.Gateway.dll"]
