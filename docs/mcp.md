# Squid MCP

Squid 通过同一 `Squid.Api` 进程和同一镜像提供只读 Streamable HTTP MCP，端点固定为 `/mcp`。首版用于查询空间、项目、环境、发布版本、部署历史及部署任务状态，不提供日志、脚本、变量、凭证、发布、回滚、取消或审批操作。

> 2026-11-23 验收通过。MCP Inspector 2.16.0 与官方 Python SDK 2.3.0 均通过协议 `2025-11-25` 完成握手、工具发现和调用。API 镜像构建和容器冒烟验证通过。

## 开关和 HTTPS

`Mcp:Enabled` 默认 `false`。关闭时 `/mcp` 返回 `404`，REST API 不受影响；开启时映射无状态 Streamable HTTP 端点。

最简开启配置如下。

```json
{
  "Mcp": {
    "Enabled": true
  }
}
```

也可以设置以下环境变量。

```bash
Mcp__Enabled=true
```

开启 MCP 需要宿主已注册 Squid 的 API Key 认证方案。当前宿主认证依赖 `Authentication:Jwt:SymmetricKey` 完成认证服务装配；这是服务端前提，不是 MCP 客户端凭证。缺少该前提时开启 MCP 会启动失败，而不是暴露匿名端点。

生产必须通过 HTTPS 访问，例如 `https://squid.example.com/mcp`。反向代理需要：

- 终止 TLS，并保留 `/mcp` 路径。
- 转发 `X-API-KEY`、`Accept`、`Content-Type`、`MCP-Protocol-Version`、`Mcp-Method`、`Mcp-Name` 和客户端提供的 `Origin`。
- 不记录、不回显 API Key、MCP 请求体或工具结果；不要对响应做会产生重复写入的缓存。
- 将超时设置为允许正常查询完成，并保持无状态请求转发。

本地 HTTP 只用于隔离环境验收，不作为生产配置。

## 浏览器来源配置（可选）

`Mcp:AllowedOrigins` 默认为空。带 `Origin` 的请求必须匹配允许列表，否则返回 `403`。不带 `Origin` 的客户端（如本地 Codex）可以保持默认值，MCP 调用仍需通过 API Key 认证。

如果网页直接访问 `/mcp`，填入该网页的来源，由协议、主机和端口组成。例如，允许 `https://approved-client.example` 发起调用时，配置如下。

```json
{
  "Mcp": {
    "Enabled": true,
    "AllowedOrigins": [
      "https://approved-client.example"
    ]
  }
}
```

用环境变量部署时，为 `Mcp__AllowedOrigins__0` 设置同一个来源。

```bash
Mcp__AllowedOrigins__0=https://approved-client.example
```

## 认证和最小业务权限

所有 `/mcp` 请求都必须携带活动用户拥有的 API Key：

```http
X-API-KEY: <temporary-api-key>
```

登录 JWT 不能替代 API Key。缺失、无效、已撤销或所属用户被禁用的 Key 返回 `401`。客户端不应把 Key 写入共享仓库、项目 `.mcp.json` 的明文值、命令行历史或日志。

首版六个工具的只读权限如下：

| 工具 | 必须的业务权限 |
| --- | --- |
| `list_spaces` | `ProjectView`（至少一个可见项目） |
| `list_projects` | `ProjectView` |
| `list_environments` | `EnvironmentView` |
| `list_releases` | `ReleaseView` |
| `list_deployments` | `DeploymentView` |
| `get_task_status` | `TaskView` |

最小内置角色是 `Project Viewer`，它包含上述读取权限。不要使用 `System Administrator`、`Space Owner` 或 `SpaceView` 作为客户端验收的前提；`SpaceView` 是系统级全量空间权限，MCP 不要求它。

授权按服务端从数据库关系解析出的真实空间、项目、项目组和环境执行：

- 同一 `ResourceScopeGrant` 内，空间、项目、项目组和环境条件按 AND 组合。
- 多个 `ResourceScopeGrant` 之间按 OR 组合。
- 不得把一个角色的项目范围与另一个角色的环境范围拼接成新权限。
- 对受限角色应显式配置项目、项目组和环境。现有作用域语义中，某一维度为空表示该维度在空间内不受限；最小权限验收必须使用非空范围。
- `/mcp` 忽略 `X-Space-Id`。客户端传入、缺失或传入冲突的 `X-Space-Id` 不应改变工具参数、授权或结果。
- 未授权资源在列表中被过滤，不占用返回条数、游标或成功摘要；详情和任务查询统一返回不可见结果。

## 客户端配置

### 临时 Key 输入（不回显、不落命令历史）

在 Bash 或 Zsh 中运行下面的命令，输入 Key 后按回车。验收时为只有所需读取权限的账户创建临时 Key。

```bash
printf 'Squid MCP API key: '
IFS= read -rs SQUID_API_KEY && export SQUID_API_KEY
printf '\n'
```

这段命令读取 Key 时不会回显，也不会将输入写入 shell 历史。验收结束后执行 `unset SQUID_API_KEY` 并撤销临时 Key。

### Codex（桌面端与 CLI）

同一台主机上的 Codex 桌面端和 CLI 共用用户级 `~/.codex/config.toml`。加入以下配置，并将 URL 换成 Squid 实例的 `/mcp` 地址。Codex 从 `SQUID_API_KEY` 环境变量读取 Key 并发送 `X-API-KEY` 请求头。

```toml
[mcp_servers.squid]
url = "https://squid.example.com/mcp"
env_http_headers = { "X-API-KEY" = "SQUID_API_KEY" }
```

先在当前终端按“临时 Key 输入”完成不回显设置。在同一个终端启动 `codex`，输入 `/mcp` 查看 `squid` 和可用工具。桌面端可在 **Settings → MCP servers** 查看服务配置。`SQUID_API_KEY` 需要在运行 Codex 的进程环境中可读，另开终端执行 `export` 不会更新已经运行的桌面端进程。

如果桌面端无法读取变量，可在个人 `~/.codex/config.toml` 中将 `env_http_headers` 替换为 `http_headers = { "X-API-KEY" = "<your-squid-api-key>" }`，然后重启桌面端。这个写法以明文保存 Key，仅用于本机个人配置。可用 `chmod 600 ~/.codex/config.toml` 限制该文件只有本人能读写。

在 Codex 会话中发起一次只读调用验证接入。

```text
使用 Squid MCP 的 list_spaces 工具列出我有权限查看的空间，pageSize 设为 1。
```

已用 Codex CLI `0.169.0` 的 `app-server` 模式连接隔离的 Squid 实例。在 `AllowedOrigins` 为空时，通过 `env_http_headers` 认证、发现六个工具并成功调用 `list_spaces`。桌面界面尚未单独实测。

### MCP Inspector

已验证安装版本：MCP Inspector `2.16.0`，临时 Node.js `24.22.3`。Inspector `2.16.0` 的 engines 要求 Node.js `^22.23.4 || ^24.22.1 || >=26.12.1`；低于该范围不应作为验收环境。

先按“临时 Key 输入”完成不回显设置，只使用临时的最小读取权限 Key。

Inspector CLI 的 `--header` 参数会包含展开后的 Key。能查看该进程参数的本机用户或工具也能读到 Key。以下示例用于隔离环境验收，请使用临时、最小权限的 Key，并在验收结束后立即撤销。

协议握手、发现和调用：

```bash
npx -y @modelcontextprotocol/inspector@2.16.0 --cli \
  --server-url https://squid.example.com/mcp \
  --transport http \
  --header "X-API-KEY: ${SQUID_API_KEY}" \
  --method tools/list

npx -y @modelcontextprotocol/inspector@2.16.0 --cli \
  --server-url https://squid.example.com/mcp \
  --transport http \
  --header "X-API-KEY: ${SQUID_API_KEY}" \
  --method tools/call \
  --tool-name list_deployments \
  --tool-args-json '{"spaceId":1,"projectId":1,"environmentId":1,"pageSize":1}'
```

Inspector 默认会把目录和客户端状态写到用户目录。隔离验收应通过 `MCP_CATALOG_PATH`、`MCP_CLIENT_CONFIG_PATH` 和临时 `HOME` 指向临时目录，避免读写个人配置。

### 官方 Python SDK

已验证版本：Python `3.14.6`、官方 MCP Python SDK `mcp 2.3.0`。使用临时虚拟环境安装：

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install 'mcp==2.3.0'
```

通过官方 Streamable HTTP 客户端注入 `X-API-KEY`，先握手再调用工具：

```python
import asyncio
import os

import httpx2
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client


async def main():
    headers = {"X-API-KEY": os.environ["SQUID_API_KEY"]}
    async with httpx2.AsyncClient(headers=headers, timeout=60) as client:
        async with streamable_http_client(
            os.environ["SQUID_MCP_URL"], http_client=client
        ) as (read_stream, write_stream):
            async with ClientSession(read_stream, write_stream) as session:
                init = await session.initialize()
                tools = await session.list_tools()
                result = await session.call_tool(
                    "list_deployments",
                    {
                        "spaceId": 1,
                        "projectId": 1,
                        "environmentId": 1,
                        "pageSize": 1,
                    },
                )
                print(init.protocol_version)
                print([tool.name for tool in tools.tools])
                print(result.structuredContent)


asyncio.run(main())
```

在当前终端按“临时 Key 输入”设置 `SQUID_API_KEY`，然后运行：

```bash
export SQUID_MCP_URL='https://squid.example.com/mcp'
python your_client.py
```

### VS Code Copilot

本机预检版本为 Visual Studio Code `1.147.0`、内置 `GitHub.copilot-chat 0.75.0`。本轮未使用 Copilot 作为验收客户端，也未启动或修改个人 VS Code profile 和配置；以下内容仅记录接入方式，不代表已实测。

编辑器管理的连接可使用工作区 `.vscode/mcp.json`，通过密码输入变量注入 Key：

```json
{
  "inputs": [
    {
      "type": "promptString",
      "id": "squid-api-key",
      "description": "Squid MCP API key",
      "password": true
    }
  ],
  "servers": {
    "squid": {
      "type": "http",
      "url": "https://squid.example.com/mcp",
      "headers": {
        "X-API-KEY": "${input:squid-api-key}"
      }
    }
  }
}
```

VS Code 1.147 的官方配置说明指出：Copilot harness 不转发编辑器 prompt 输入的密钥。使用原生 Copilot harness 时，改在工作区 `.mcp.json` 使用宿主环境变量引用，并从已设置该变量的终端启动 VS Code。以下配置未经本轮 Copilot 实测：

```json
{
  "mcpServers": {
    "squid": {
      "type": "http",
      "url": "https://squid.example.com/mcp",
      "headers": {
        "X-API-KEY": "${SQUID_API_KEY}"
      },
      "tools": ["*"]
    }
  }
}
```

先按“临时 Key 输入”设置最小读取权限 Key，再从同一终端运行 `code /path/to/workspace`。

若工作区表单拒绝 `${...}`，在源码 `.mcp.json` 中编辑引用，不要把提示框限制当作可写入明文 Key 的理由。验收结束后执行 `unset SQUID_API_KEY` 并撤销临时 Key。

### OAuth-only 客户端

Squid MCP 首版只接受 `X-API-KEY`，不提供 OAuth 发现、授权码流程或动态客户端注册。只能通过 OAuth 登录、且不能发送自定义 HTTP 请求头的客户端不能直接接入；不要配置 OAuth 端点来假装支持。需要使用能够安全注入请求头的客户端，或等待后续 OAuth 设计。

## 工具契约

列表默认 `pageSize=20`，允许 `1` 至 `100`。结果按稳定顺序分页，响应包含 `items` 和可继续请求的 `nextCursor`；没有后续页时 `nextCursor` 为 `null`，JSON 中为空值。`cursor` 是不透明值，最长接受 4096 个编码字符，绑定工具名、完整查询条件和稳定排序位置（发布、部署使用 `createdAt` + `id`，其他列表使用 `id`）。跨工具、跨查询或无效游标返回 `INVALID_ARGUMENT`。关键词查询摘要按 Core 的实际搜索语义生成：去掉首尾空白并转小写，不做 Unicode NFC 归一化；仅规范化形式不同的关键词不会共用游标。

| 工具 | 输入 | 结果 |
| --- | --- | --- |
| `list_spaces` | 分页 | 至少存在一个可见项目的空间：`id`、`name` |
| `list_projects` | `spaceId`，可选 `keyword`、分页 | `id`、`name`、`slug`；名称或 slug 匹配关键词 |
| `list_environments` | `spaceId`、`projectId`，可选 `keyword`、分页 | `id`、`name` |
| `list_releases` | `spaceId`、`projectId`、分页 | `id`、`version`、`channelId`、`createdAt` |
| `list_deployments` | `spaceId`、`projectId`、`environmentId`、分页 | `items`：`id`、`releaseId`、`currentReleaseVersion`、`taskId`、`state`、`createdAt`、`completedAt`；另含 `latestSuccessfulDeployment` |
| `get_task_status` | `spaceId`、`taskId` | `taskId`、`deploymentId`、`state`、`startedAt`、`completedAt` |

工具声明为只读，但只读声明不替代服务端授权检查。工具成功时返回简短文本和 `structuredContent`；业务失败返回 `isError: true`，并带稳定 `code`：

- `INVALID_ARGUMENT`：参数、分页或游标无效。
- `NOT_FOUND_OR_FORBIDDEN`：资源不存在或不可见，不区分二者。
- `TEMPORARY_FAILURE`：可重试的服务端临时故障。

每个工具的 `outputSchema` 使用 `oneOf` 分别声明成功对象和 `{ code, message }` 错误对象。业务错误返回 `isError: true`，同时提供可读文本和 `structuredContent`。Inspector CLI 遇到业务错误时仍输出完整 JSON，但进程退出码为 `5`；应以 JSON 中的 `isError` 和 `code` 为准，不要把该退出码误判为协议或传输失败。

空列表是成功结果。结果只包含白名单字段，不返回日志、脚本、变量、凭证、堆栈或查询细节。已认证的 SDK POST 在协议层返回 HTTP 400 时，审计归类为 `protocol_error`，且不记录请求体、工具参数或结果。

## 版本和时间语义

需要区分三类版本：

1. **MCP 协议版本**：由客户端与 SDK 在 `initialize` 协商。只能在真实握手后记录客户端实际使用的版本，不能由 Squid 发布版本推导。
2. **Squid 版本**：API 镜像或应用版本，不在六个工具中作为部署版本返回。
3. **发布版本**：`list_releases.version` 是当前发布记录的版本号；`list_deployments.currentReleaseVersion` 是部署记录关联发布的当前版本号。

`currentReleaseVersion` 不是部署发生时的历史快照。关联发布改号后，后续查询返回改号后的当前值；关联发布缺失时，部署记录仍保留，字段返回 `null`。历史版本快照不属于首版契约。

部署历史保留仍在数据库中的部署记录。`list_deployments`：

- 第一页首项是该授权范围内按 `createdAt`、`id` 倒序的最近一次部署尝试。
- 只有任务与部署记录形成唯一关联时，历史行才采用该任务状态。
- `latestSuccessfulDeployment` 在同一授权范围的完整部署集合中选取唯一关联任务状态为 `Success` 的第一条，不受当前页限制。
- 最近一次尝试和最近一次成功可能指向不同记录；成功摘要没有记录时为 `null`。
- 任务无法匹配到该部署或同一 `taskId` 关联多条部署记录时，历史行保留原始 `taskId`，`state` 返回 `Unknown`，`completedAt` 为 `null`。
- 任务状态缺失或无法识别时，`state` 返回 `Unknown`。`Unknown` 记录不会进入成功摘要。
- `get_task_status` 同样只解析唯一任务关联；重复关联按不可见处理，不猜测对应部署。
- 这些字段描述 Squid 中的记录，不声称验证了目标机器上的真实运行版本或产物内容。

时间字段使用带时区的 ISO 8601 字符串。`createdAt` 表示记录创建时间，`completedAt` 表示关联任务完成时间；排序使用部署记录的 `createdAt` 和 `id`。

## 按名称查询示例

1. 调用 `list_projects`，`spaceId` 使用已授权空间，`keyword` 使用项目名或 slug。
2. 若同名项目返回多个结果，由用户按 ID 消歧，不自动选择。
3. 调用 `list_environments`，传入已选项目 ID 和 `keyword` 环境名。
4. 若同名环境返回多个结果，由用户按 ID 消歧。
5. 调用 `list_deployments`，传入空间、项目和环境 ID，读取最近一次尝试及 `latestSuccessfulDeployment`。
6. 使用部署的 `taskId` 调用 `get_task_status`。

## 验收记录

**最终验证时间：2026-11-23 CST。**

### 构建和自动化

| 检查 | 结果 |
| --- | --- |
| Release 构建 | 通过，`0 errors` |
| MCP 单元测试 | `12/12` 通过 |
| MCP 集成测试 | `41/41` 通过 |
| MCP HTTP E2E | `17/17` 通过；协议错误审计最终增量落盘后再次执行 |
| `Dockerfile.Api` | 通过。构建 `squid-api:mcp-local` 并用该镜像验证开关、鉴权、握手、六个工具发现和只读调用。 |

### 客户端实测

| 客户端 | 版本 | 协议版本 | 握手 | `tools/list` | 成功调用 | 预期业务错误 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| MCP Inspector | 2.16.0 / Node 24.22.3 | `2025-11-25` | 通过 | 六个工具 | `list_deployments` 通过 | `get_task_status` 返回 `NOT_FOUND_OR_FORBIDDEN`，文本可读 | 通过 |
| 官方 Python SDK | `mcp 2.3.0` / Python 3.14.6 | `2025-11-25` | 通过 | 六个工具 | `list_deployments` 通过 | `get_task_status` 返回 `NOT_FOUND_OR_FORBIDDEN`，文本可读；超长游标返回 `INVALID_ARGUMENT` | 通过 |
| VS Code Copilot | VS Code 1.147.0 / Copilot Chat 0.75.0 | 未实测 | 未实测 | 未实测 | 未实测 | 未实测 | 未实测 |

两种客户端均调用了 `list_deployments`。已认证的 HTTP 400 请求在审计中归类为 `protocol_error`，日志只保留审计字段。测试数据中的最近一次尝试为部署 `50003`/任务 `60003` `Failed`，最近成功为部署 `50002`/任务 `60002` `Success`，`currentReleaseVersion=2026.11.2`；无成功环境返回 `null`，缺任务部署返回 `Unknown`，跨范围项目/环境返回空集合。

### 验收清单

- [x] Release 构建和 API 镜像构建通过。
- [x] 容器关闭时 `/mcp` 返回 `404`，REST 保持可用；开启后合法 Key 可握手、发现六个工具和调用。
- [x] 缺失、无效、撤销 Key 和仅 JWT 请求返回 `401`；禁止 Origin 返回 `403`。
- [x] 限制账号只看到授权项目、项目组和环境；跨范围结果不进入列表、错误文本或成功摘要。
- [x] 最近失败尝试、最近成功摘要、无成功摘要、任务缺失和发布改号场景通过。
- [x] 应用日志、临时客户端配置和共享配置中不存在 API Key、请求体或工具结果。
- [x] 测试 API Key 已撤销，撤销后请求返回 `401`；客户端环境变量和临时秘密文件已清除。

## 参考资料

- [Codex MCP 配置](https://developers.openai.com/codex/mcp)
- [MCP Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)
- [MCP Authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)
- [C# SDK HTTP transport](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html)
- [VS Code MCP configuration](https://code.visualstudio.com/docs/copilot/reference/mcp-configuration)
- [MCP Inspector](https://github.com/modelcontextprotocol/inspector)
- [MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk)
