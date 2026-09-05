ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.11
FROM ${SDK_IMAGE} AS build
ARG PROJECT=src/RelayLab.Api/RelayLab.Api.csproj
WORKDIR /source
COPY . .
RUN dotnet restore "$PROJECT" --locked-mode --configfile NuGet.Config
RUN dotnet publish "$PROJECT" -c Release --no-restore -o /app /p:UseAppHost=false

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
