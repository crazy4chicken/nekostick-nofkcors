# Nekostick.NoFkCors

## 项目简介

基于 nekostick 宿主的扩展，启用期间拦截所有 CORS 相关响应并返回 allow-all 策略。

## 工作原理

启用时 `StartAsync` 注册 Return 阶段全局路由钩子：

- preflight（OPTIONS + Origin + Access-Control-Request-Method）直接替换为 204 + allow-all CORS 头（`Access-Control-Allow-Origin: *`、`Access-Control-Allow-Methods: *`、`Access-Control-Allow-Headers: *`、`Access-Control-Max-Age: 60`）；`Max-Age: 60` 让浏览器对同一 URL+方法的 preflight 缓存 60 秒以控制 preflight 数量，禁用扩展后最迟 ~60 秒恢复原 CORS 策略；
- 普通 CORS 响应抹掉上游所有 `Access-Control-*` 头后盖上 `Access-Control-Allow-Origin: *` / `Access-Control-Expose-Headers: *`，其余头与响应体原样保留；
- 非 CORS 请求原样放行；
- 钩子内部全程 fail-open：任何异常都不会让宿主取消请求；
- 禁用时 Host 停止扩展 generation，钩子随代次自动注销，无需手动反注册。

## 已知限制

以下流量直通（Continue），不盖章 CORS 头：

- WebSocket / 101 升级响应：替换会被宿主合法 fail-closed（499 + Abort）；
- streaming handler 路由（SSE 等）：钩子只能拿到空体元数据快照，替换会中止进行中的流（特征判定：空 body 且无 `Content-Length`）；
- body 达到 64KiB 快照上限的响应：快照可能被宿主截断，替换会污染大响应；
- allow-all 使用通配符 `*`，不覆盖 credentialed 请求。

宿主版本提示：路由钩子在 `7dcbc9ca`（2026-10-07）之前对超限流量 fail-closed 中止；旧版宿主上本扩展会让 >64KiB 请求/响应与 WebSocket 中断。建议宿主不早于该提交。

## 构建与部署

```
dotnet build -c Release
```

把输出目录中的 `Nekostick.NoFkCors.dll` 与 `manifest.json` 放进 Host 的 `extensions/nekostick.nofkcors/` 目录。需要 Host API >= 1.3，Contracts 1.4.0。

CI 每次推送产出 artifact `nekostick-nofkcors.<shortsha>.zip`，内容平铺在压缩包根部（不含嵌套目录），直接解压到 `extensions/nekostick.nofkcors/` 即可。

## License

AGPL-3.0，见 LICENSE。