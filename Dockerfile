FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Project no.1.csproj", "./"]
RUN dotnet restore "Project no.1.csproj"
COPY . .
RUN dotnet publish "Project no.1.csproj" --configuration Release --output /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
COPY --from=build /app/publish .
COPY --from=build /src/wwwroot ./wwwroot
ENTRYPOINT ["dotnet", "Project no.1.dll"]
