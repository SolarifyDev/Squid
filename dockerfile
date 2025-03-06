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

# 使用基础镜像
FROM octopusdeploy/tentacle

# 设置环境变量
ENV ListeningPort="10931"
ENV ServerApiKey="API-WZ27UDXXAPCKUPZSH1WTG8YC80G"
ENV TargetEnvironment="Test"
ENV TargetRole="app-server"
ENV ServerUrl="https://octopus.example.com"
ENV PublicHostNameConfiguration="ComputerName"
ENV ACCEPT_EULA="Y"

# 暴露端口
EXPOSE 10931

# 设置容器启动时执行的命令
CMD ["tentacle"]

ENTRYPOINT ["dotnet", "Squid.Api.dll"]