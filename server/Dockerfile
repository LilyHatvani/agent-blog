# Agent Blog image. Build from the repo root:
#   docker build -f server/Dockerfile -t agent-blog .
# One process: the public site, the owner API, the MCP door. Shape from Relay's server/Dockerfile.

# ===== The owner's app =====
# pwa/ (Preact + Vite), built here and served by the server at /admin (Auth/AdminApp.cs).
# The app is static files, so it builds once on the builder's own platform for every image.
FROM --platform=$BUILDPLATFORM node:22-bookworm-slim AS pwa
WORKDIR /pwa
COPY pwa/package.json pwa/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY pwa/ ./
RUN npm run build

# ===== Build =====
# global.json pins 10.0.100 with rollForward latestFeature, so any .NET 10 SDK image builds it.
# The SDK runs on the builder's platform and compiles for the target one (linux/amd64 or linux/arm64), so a
# multi-platform release needs no emulation. A plain `docker build` targets the machine it runs on.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json ./
COPY server/Directory.Build.props server/
COPY server/AgentBlog.Server/AgentBlog.Server.csproj server/AgentBlog.Server/
RUN dotnet restore server/AgentBlog.Server/AgentBlog.Server.csproj -a ${TARGETARCH:-amd64}

COPY server/AgentBlog.Server/ server/AgentBlog.Server/
# Railway passes the deployed commit as a build variable when the Dockerfile declares it; the release workflow passes
# it too. /version then names it.
ARG RAILWAY_GIT_COMMIT_SHA=
RUN dotnet publish server/AgentBlog.Server/AgentBlog.Server.csproj -c Release -o /app/publish --no-restore \
    -a ${TARGETARCH:-amd64} --self-contained false -p:UseAppHost=false -p:BLOG_BUILD_SHA=${RAILWAY_GIT_COMMIT_SHA}

# ===== Runtime =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Npgsql probes for GSSAPI; without these libraries it logs an error on every new connection (Relay's image does the same).
RUN apt-get update && apt-get install -y --no-install-recommends libkrb5-3 libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
COPY --from=pwa /pwa/dist ./wwwroot/admin

# The base image's non-root user.
USER $APP_UID

# Listens on 8080; a platform that sets PORT (Railway) overrides it (Program.cs).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AgentBlog.Server.dll"]
