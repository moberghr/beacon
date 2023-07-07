FROM mcr.microsoft.com/dotnet/aspnet:7.0-alpine AS base
WORKDIR /app

# Disable the invariant mode (set in base image)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

FROM base as semantico-api
COPY publish .
ENTRYPOINT ["dotnet", "Semantico.Api.dll"]