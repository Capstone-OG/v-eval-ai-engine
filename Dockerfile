# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy gRPC definitions and project files
COPY ["grpc/", "grpc/"]
COPY ["All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.API/V-Eval-Ai_Engine.API.csproj", "All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.API/"]
COPY ["All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.Application/V-Eval-Ai_Engine.Application.csproj", "All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.Application/"]
COPY ["All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.Domain/V-Eval-Ai_Engine.Domain.csproj", "All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.Domain/"]
COPY ["All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.Infrastructure/V-Eval-Ai_Engine.Infrastructure.csproj", "All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.Infrastructure/"]
RUN dotnet restore "All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.API/V-Eval-Ai_Engine.API.csproj"

# Copy full source and publish Release artifact
COPY ["All Services/V-Eval-Ai_Engine/", "All Services/V-Eval-Ai_Engine/"]
WORKDIR "/src/All Services/V-Eval-Ai_Engine/V-Eval-Ai_Engine.API"
RUN dotnet publish "V-Eval-Ai_Engine.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: ASP.NET Core Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 5104
ENV ASPNETCORE_URLS=http://+:5104
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "V-Eval-Ai_Engine.API.dll"]
