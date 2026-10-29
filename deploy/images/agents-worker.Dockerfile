# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ENV PATH="/root/.dotnet/tools:${PATH}"
WORKDIR /source
COPY . .
RUN dotnet tool install --global Paket --version 10.3.1 \
    && dotnet restore src/Nao.Agents.Worker/Nao.Agents.Worker.fsproj \
    && dotnet publish src/Nao.Agents.Worker/Nao.Agents.Worker.fsproj \
        --configuration Release \
        --no-restore \
        --output /app \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30
ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/jichang/Nao" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.title="Nao agents worker"
WORKDIR /app
COPY --from=build --chown=65532:65532 /app .
USER 65532:65532
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "Nao.Agents.Worker.dll"]
