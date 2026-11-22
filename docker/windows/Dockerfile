# escape=`
# ps-bash in a Windows container. The release is self-contained (its own .NET runtime and PowerShell SDK), so the
# image needs nothing else. Build from the repo root:
#   docker build -f docker/windows/Dockerfile --build-arg PSBASH_VERSION=0.15.1 -t ps-bash:win .
# BASE: Nano Server (smallest) or Server Core. With process isolation the tag must match the host's Windows version.
ARG BASE=mcr.microsoft.com/windows/nanoserver:ltsc2022
FROM ${BASE}
ARG PSBASH_VERSION=0.15.1
# The user the image runs as afterwards: ContainerUser (Nano Server's default), or ContainerAdministrator (Server Core's).
ARG RUNAS=ContainerUser

# Nano Server's ContainerUser cannot write C:\, so install as the administrator.
USER ContainerAdministrator
RUN curl.exe -fsSL -o C:\ps-bash.zip https://github.com/standardbeagle/ps-bash/releases/download/v%PSBASH_VERSION%/ps-bash-v%PSBASH_VERSION%-win-x64.zip `
 && mkdir C:\ps-bash `
 && tar.exe -xf C:\ps-bash.zip -C C:\ps-bash `
 && del C:\ps-bash.zip `
 && setx /M PATH "%PATH%;C:\ps-bash"
USER ${RUNAS}

ENV SHELL=C:/ps-bash/ps-bash.exe
# Build steps use a private host that ps-bash stops, and whose socket file it removes, before the step ends. The
# shared host would outlive the step and be killed with the build container, leaving its socket file in the layer,
# and Windows cannot commit a layer holding a socket file (hcsshim::ImportLayer failed ... cannot find the path
# specified). An ARG is not kept in the image, so containers run from it still share one host across commands.
ARG PSBASH_PER_INVOCATION=1
# Every RUN line after this one is bash.
SHELL ["C:\\ps-bash\\ps-bash.exe", "-c"]
RUN echo "ps-bash $BASH_VERSION ready"
CMD ["C:\\ps-bash\\ps-bash.exe"]
