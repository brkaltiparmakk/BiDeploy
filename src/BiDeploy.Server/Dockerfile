FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/BiDeploy.Core/ src/BiDeploy.Core/
COPY src/BiDeploy.Server/ src/BiDeploy.Server/
RUN dotnet publish src/BiDeploy.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BiDeploy.Server.dll"]
