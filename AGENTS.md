# 项目约束

## Git 提交

**没有用户明确要求，不得自行 `git commit` / `git push` / 打 tag / 建 Release。** 改代码、编译通过、测试通过都不构成提交授权；完成后把改动留在工作区，告知用户改了哪些文件，等用户下指令。

「发布」隐含 commit + push + tag + Release（见下节）；除此之外，提交与推送必须单独明确授权。

若用户已授权提交，只提交本次任务相关的改动，不要把工作区里无关的脏文件一起带上。

## 发布更新

**只有用户明确下达「发布」指令时才执行发布。** 编译、测试、改版本号、修 bug 都不等于发布授权；验证用的临时版本号改完要还原，不要顺手发版。

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
- 安装包命名 `shuiyue-<版本>-win-x64.zip`，平台与架构写进文件名
- 只支持 64 位 Windows（按 x64 编译，32 位系统无法运行）
- 主页由他人维护，只做部署与术语同步，不重做样式
