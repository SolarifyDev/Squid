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

# 使用 Octopus Deploy 的 Tentacle 镜像
FROM docker.packages.octopushq.com/octopusdeploy/tentacle:${TENTACLE_VERSION}

# 设置环境变量
ENV ServerUsername="${OCTOPUS_ADMIN_USERNAME}"
ENV ServerPassword="${OCTOPUS_ADMIN_PASSWORD}"
ENV TargetEnvironment="Development"
ENV TargetRole="app-server"
ENV ServerUrl="http://octopus-server:8080"

# 创建挂载点
RUN mkdir -p /Applications && mkdir -p /TentacleHome

# 挂载卷（Dockerfile 不支持直接挂载卷，需要在运行容器时指定）
VOLUME [ "/Applications", "/TentacleHome" ]

# 保持容器运行
CMD ["tail", "-f", "/dev/null"]

ENTRYPOINT ["dotnet", "Squid.Api.dll"]