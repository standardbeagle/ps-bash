# ps-bash in a Linux container. The release is self-contained (its own .NET runtime and PowerShell SDK); the
# runtime-deps base image carries the native libraries .NET needs (ICU, OpenSSL). Build from the repo root:
#   docker build -f docker/linux/Dockerfile --build-arg PSBASH_VERSION=0.15.1 -t ps-bash:linux .
ARG PSBASH_VERSION=0.15.1

FROM debian:bookworm-slim AS fetch
ARG PSBASH_VERSION
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /tmp/ps-bash
RUN zip="ps-bash-v${PSBASH_VERSION}-linux-x64.zip" \
 && url="https://github.com/standardbeagle/ps-bash/releases/download/v${PSBASH_VERSION}/${zip}" \
 && curl -fsSLO "$url" \
 && curl -fsSLO "$url.sha256" \
 && sha256sum -c "$zip.sha256" \
 && unzip -q "$zip" -d /opt/ps-bash

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
COPY --from=fetch /opt/ps-bash /opt/ps-bash
ENV PATH="/opt/ps-bash:${PATH}" SHELL=/opt/ps-bash/ps-bash
# Every RUN line after this one is bash.
SHELL ["/opt/ps-bash/ps-bash", "-c"]
RUN echo "ps-bash $BASH_VERSION ready"
CMD ["/opt/ps-bash/ps-bash"]
