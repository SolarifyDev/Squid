FROM registry-vpc.cn-hongkong.aliyuncs.com/wiltechs/sdk:8.0 AS build-env

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
FROM registry-vpc.cn-hongkong.aliyuncs.com/wiltechs/aspnet:8.0

USER root

# 安装 Tentacle
RUN apt-get update && \
    apt-get install -y wget && \
    wget https://download.octopusdeploy.com/linux-tentacle/tentacle-6.0.0-linux-x64.tar.gz -O tentacle.tar.gz && \
    mkdir /opt/tentacle && \
    tar -xvf tentacle.tar.gz -C /opt/tentacle && \
    rm tentacle.tar.gz && \
    ln -s /opt/tentacle/tentacle /usr/local/bin/tentacle

WORKDIR /app

# 复制构建结果
COPY --from=build-env /app/out .

# 设置 Tentacle 环境变量（根据需要调整）
ENV OCTOPUS_SERVER_URL="http://localhost:8080"
ENV OCTOPUS_API_KEY="your-api-key"
ENV OCTOPUS_SPACE="Default"
ENV OCTOPUS_ROLE="web-server"
ENV OCTOPUS_ENVIRONMENT="Test"

# 启动 Tentacle 和应用程序
CMD tentacle configure --instance "Tentacle" --server "$OCTOPUS_SERVER_URL" --apiKey "$OCTOPUS_API_KEY" --space "$OCTOPUS_SPACE" --role "$OCTOPUS_ROLE" --environment "$OCTOPUS_ENVIRONMENT" && \
    tentacle service --instance "Tentacle" --install --start

ENTRYPOINT ["dotnet", "Squid.Api.dll"]