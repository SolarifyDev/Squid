FROM registry-vpc.cn-hongkong.aliyuncs.com/wiltechs/sdk:9.0 AS build-env

WORKDIR /app

COPY ./src/Squid.Api ./build/Squid.Api
COPY ./src/Squid.Core ./build/Squid.Core
COPY ./src/Squid.Message ./build/Squid.Message
COPY ./NuGet.Config ./build

RUN dotnet publish build/Squid.Api -c Release -o out

# 运行时阶段
FROM registry-vpc.cn-hongkong.aliyuncs.com/wiltechs/aspnet:9.0

WORKDIR /app

COPY --from=build-env /app/out .

EXPOSE 8080
EXPOSE 443

USER root

ENTRYPOINT ["dotnet", "Squid.Api.dll"]