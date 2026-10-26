# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG MINVERVERSIONOVERRIDE
RUN if [ -z "$MINVERVERSIONOVERRIDE" ]; then \
      echo "MINVERVERSIONOVERRIDE is required. Calculate it before docker build: dotnet tool restore && dotnet minver --tag-prefix v" >&2; \
      exit 1; \
    fi
ENV MINVERVERSIONOVERRIDE=$MINVERVERSIONOVERRIDE
WORKDIR /src
# MSBuild/CPM manifests first so `dotnet restore` resolves versions and caches well
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PS5LM.Companion.Core/PS5LM.Companion.Core.csproj src/PS5LM.Companion.Core/
COPY src/PS5LM.Companion.Infrastructure/PS5LM.Companion.Infrastructure.csproj src/PS5LM.Companion.Infrastructure/
COPY src/PS5LM.Companion.App/PS5LM.Companion.App.csproj src/PS5LM.Companion.App/
RUN dotnet restore src/PS5LM.Companion.App/PS5LM.Companion.App.csproj
COPY src/ src/
RUN dotnet publish src/PS5LM.Companion.App/PS5LM.Companion.App.csproj -c Release -o /app/publish /p:UseAppHost=false /p:MinVerVersionOverride="$MINVERVERSIONOVERRIDE"

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG MINVERVERSIONOVERRIDE
LABEL org.opencontainers.image.version=$MINVERVERSIONOVERRIDE
WORKDIR /app
COPY --from=build /app/publish .
# The aspnet base image already sets HTTP_PORTS=8080; ASPNETCORE_URLS would override it with a warning.
ENV Companion__DataDirectory=/data
EXPOSE 8080
VOLUME ["/data"]

RUN useradd -u 1001 -M -s /sbin/nologin appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /app /data
USER appuser

# aspnet runtime images ship neither wget nor curl: probe via the app itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "PS5LM.Companion.App.dll", "--health-check"]

ENTRYPOINT ["dotnet", "PS5LM.Companion.App.dll"]
