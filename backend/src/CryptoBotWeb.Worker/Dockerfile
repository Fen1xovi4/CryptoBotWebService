FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/worker/ .
ENTRYPOINT ["dotnet", "CryptoBotWeb.Worker.dll"]
