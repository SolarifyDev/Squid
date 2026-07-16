# Deploy a Package V3 Implementation Plan (Entry)

> 入口文档。完整 task 拆分需在 V3 design 批准后，用 writing-plans 重写本文件。

**Goal:** 完成 Deploy a Package 收尾：配置改写、安装策略、非 NuGet archive 包源，全部默认常显且真实生效。

**Design:** `docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md`（design-approved）

**Scope (no defer):**
1. .NET Configuration Variables
2. .NET Configuration Transforms
4. Substitute in Files
5. Structured / JSON Configuration Variables
6. 非 NuGet 包源（archive 可安装；Docker/Helm 显式拒绝）
7. purge / preserve
8. skip-if-already-installed
9. 旧版本 retention
10. 自动回滚 / current 指针

**Not in scope:** Configure features 真体系；步骤内选版本。

**Waves:**
1. W1 配置改写
2. W2 安装策略
3. W3 包源
4. W4 回归收口

**Next:**
1. writing-plans 按 design 重写本文件为可执行任务计划
2. 用户确认 plan 后按 SDD/inline 执行
