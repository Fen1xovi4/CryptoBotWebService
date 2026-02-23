FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/api/ .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "CryptoBotWeb.Api.dll"]
