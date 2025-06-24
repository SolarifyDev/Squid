FROM registry-vpc.cn-hongkong.aliyuncs.com/wiltechs/sdk:9.0 AS build-env

USER root

WORKDIR /app
EXPOSE 8080
EXPOSE 443

COPY ./src/Squid.Api ./build/Squid.Api
COPY ./src/Squid.Core ./build/Squid.Core
COPY ./src/Squid.Infrastructure ./build/Squid.Infrastructure
COPY ./NuGet.Config ./build

RUN dotnet publish build/Squid.Api -c Release -o out

# 运行时阶段
FROM registry-vpc.cn-hongkong.aliyuncs.com/wiltechs/aspnet:9.0

USER root

ENTRYPOINT ["dotnet", "Squid.Api.dll"]