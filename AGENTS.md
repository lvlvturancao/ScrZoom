# 项目约束

## 发布更新

「发布更新」指同时完成 **Cloudflare Pages 部署** 和 **GitHub Release**，两处必须是同一版本，不能只发一处。

固定顺序：

1. 改 `app.cs` 里的 `AppVersion` 与 `AppVersionCode`（两处同改，这是唯一版本来源）
2. `build.ps1` 编译
3. `publish.ps1` 生成安装包与 `update.json`
4. `deploy-pages.ps1` 部署 Cloudflare
5. `git commit` + `git push` + 打同名 tag
6. 创建 GitHub Release，附件上传 `publish.ps1` 生成的那个 zip

要求：

- GitHub Release 的附件必须与 `update.json` 里的 `size` 和 `sha256` 一致
- 发布前删掉 `cloudflare-pages/public/` 下旧版本的 zip，避免一起部署
- 部署后核对：`update.json` 的版本号与校验和、zip 能完整下载、主页显示新版本
- 主页由他人维护，只做部署与术语同步，不重做样式
