# ISETOOLS

> 适用于北航计组BUAA CO相关课程。

Xilinx ISE 14.7 在 Windows 10/11 上的**修复 + 启动器封装 + 回滚**工具，**使用了Deepseek V4.1 Flash 1017开发**，实测在Windows 11 25H2上可完成全部功能。
请谨慎使用，在不同机型上运行造成风险与开发者无关。

ISE 14.7 是 2013 年的软件，在 Windows 11 机器上运行时可能由于VC++ 2008目录不一致产生环境兼容性问题。ISETOOLS 把这两处修复做成一次性的向导，并交付一个 `.exe` 启动器。

- 版本：1.0.0
- 平台：Windows 10 / 11（x64）
- 运行时依赖：无（.NET Framework 4.x 是 Windows 自带组件）
- 你需要准备的：
    - 已完成安装的ISE 14.7本体
    - 一份`Xilinx_ise.lic`许可证文件

---

## 1. 功能

| 功能 | 说明 |
|---|---|
| 选择 ISE 安装位置 | 图形向导，可浏览选择，也可从桌面上已有的 ISE 快捷方式自动探测（读取 `.lnk` 的 `TargetPath`） |
| 一键修复 | 自动应用下面两处修复，并把原始值记录到 journal 以便回滚 |
| 选择许可证文件 | 自动在常见目录找 `*.lic`，并做一次轻量校验（内容须包含 `xilinxd`） |
| 启动器 `.exe` | 生成 `ISETOOLS.exe`，双击即启动 ISE；快捷方式指向它，因此**可以固定到任务栏和开始菜单** |
| 快捷方式 | 桌面 `Xilinx ISE 14.7`、开始菜单 `ISETOOLS` 文件夹（含启动、修复工具、回滚三项） |
| 回滚 | 一键把注册表、环境变量、备份过的 ISE 文件、快捷方式全部恢复到修复前状态 |
| 自检 | `ISETOOLS.exe --selftest`，12 项逻辑检查，退出码 0/1 |

### 修复内容

**修复 1：绕过 VC++ 2008 运行库自检误报**

`ise.exe` 内嵌 manifest 要求 SxS 程序集 `Microsoft.VC90.CRT 9.0.30729.1`，启动时按**精确版本前缀**扫
`%SYSTEMROOT%\WinSxS\amd64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_9.0.*`。Windows 更新会把该程序集整合成
`9.0.30729.9635`，于是 ISE 误判「运行库没装」，弹出：

```
VC++ 2008 runtime libraries are not installed.
```

点确定后进程直接退出 —— 这是最常见的「点确定后就没反应」。而运行库其实是好的（`MSVCR90.dll` 能正常加载，
还有 `amd64_policy.9.0.microsoft.vc90.crt_...` 策略重定向）。

→ 工具设置机器级环境变量 **`XILINX_VC_CHECK_NOOP=1`**（`ise.exe` 内置的官方开关，跳过该自检）。

**修复 2：把坏掉的 P9NP 网络提供程序从枚举顺序里移除**

ISE 可移植层给路径构造 `Port::FileName` 时会调用 `WNetEnumResourceW` 枚举网络连接。Windows 会把请求转给
`p9np.dll`（Plan 9 / WSL 9P 网络提供程序），该提供程序异常时会永久阻塞：

```
NtWaitForSingleObject ← WaitForSingleObjectEx ← NPEnumResource(p9np.dll)
  ← WNetEnumResourceW(MPR.dll) ← Port::FileName::SetFullName(libPortability.dll)
```

结果是 `ise.exe` 进程存在、CPU 几乎为 0、**永远不出窗口**（实测存活 21 分钟仍无窗口）。

→ 工具把 `P9NP` 从这两处移除（并备份原值）：

```
HKLM\SYSTEM\CurrentControlSet\Control\NetworkProvider\Order
HKLM\SYSTEM\CurrentControlSet\Control\NetworkProvider\HwOrder
```

> 只有在用 WSL 的 `\\wsl.localhost` 时才会用到 P9NP；回滚会把它加回去。

**许可证**

`HOSTID=ANY`、`uncounted`、`permanent` 的 license 文件可直接读，不需要起 `lmgrd` 服务。
工具设置机器级环境变量 **`XILINXD_LICENSE_FILE=<许可证路径>`**。

---

## 2. 使用

1. 把 `ISETOOLS.exe`（或 `ISETOOLS-1.0.0.zip` 解出来的文件）放到任意位置，双击运行。运行时请关闭杀毒软件。
2. 首次运行会弹出向导（不是管理员时会自动请求 UAC 提权 —— 修复要改 HKLM，必须提权）。
3. 确认「ISE 安装位置」指向 `...\14.7\ISE_DS`。
4. 确认你已经准备好的「许可证文件」。
5. 点 **应用修复并安装**。完成后：
   - 启动器安装到 `%ProgramData%\ISETOOLS\ISETOOLS.exe`
   - 桌面出现 `Xilinx ISE 14.7` 快捷方式（指向 .exe，可固定到任务栏/开始菜单）
   - 开始菜单出现 `ISETOOLS` 文件夹
6. 环境变量的改动**对已经运行的程序无效**。若发现旧行为仍在，注销/重启一次。

### 回滚

- 开始菜单 → `ISETOOLS` → `回滚（恢复修复前状态）`，或运行 `ISETOOLS.exe --rollback`。

回滚会恢复：`NetworkProvider\Order`/`HwOrder` 原值、两个环境变量原值（原本不存在则删除）、
备份过的 `libPortability.dll.orig`、删除所有由本工具创建的快捷方式。
回滚**不会**删除 `%ProgramData%\ISETOOLS` 里的日志和 journal（方便事后排查），可手动删除。

### 命令行

```
ISETOOLS.exe               启动 ISE（未安装过则打开向导）
ISETOOLS.exe --setup       打开修复向导
ISETOOLS.exe --rollback    回滚
ISETOOLS.exe --selftest    自检（退出码 0 = 全部通过）
```

### 文件位置

| 路径 | 内容 |
|---|---|
| `%ProgramData%\ISETOOLS\ISETOOLS.exe` | 安装后的启动器 |
| `%ProgramData%\ISETOOLS\journal.txt` | 修复记录（回滚依据：原注册表值、原环境变量值、快捷方式路径） |
| `%ProgramData%\ISETOOLS\isetools.log` | 操作日志 |
| `%TEMP%\isetools-launch-<pid>.bat` | 启动时临时生成，用完即删 |

---

## 3. 构建

**不需要安装 SDK。** 只用 Windows 自带的 .NET Framework 4.x 编译器 `csc.exe`，产物是单文件 exe（约 27 KB）。

```powershell
# 完整构建：编译 + 跑内建自检 + 把 README 复制到 dist
.\build.ps1

# 只编译
.\build.ps1 -NoTest

# 额外出一个发布压缩包 dist\ISETOOLS-1.0.0.zip（exe + README.md）
.\build.ps1 -Release
```

脚本会自动找 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（找不到则退回 32 位路径）。

等价的手工命令：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu `
  /optimize+ /warn:4 /win32manifest:src\app.manifest `
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll /out:dist\ISETOOLS.exe src\ISETOOLS.cs
```

### 工程结构

```
ISETOOLS.cs          单文件全部逻辑（C# 5，刻意不用 C# 6+ 语法以兼容老 csc）
app.manifest         asInvoker + dpiAware。启动器不能常驻提权，否则 ISE 会以管理员身份运行，
                     既没必要也会影响任务栏固定；setup/rollback 用 ShellExecute "runas" 自我提权。
build.ps1            构建 + 自检 + 打包
dist/                产物
```

### 代码结构（`src/ISETOOLS.cs`）

| 类型 | 职责 |
|---|---|
| `Program` | 解析 `--setup` / `--rollback` / `--selftest`，默认走启动路径 |
| `Admin` | 判断是否提权；未提权时用 `runas` 重启自身 |
| `Journal` | `key=value` 形式的修复记录，`{{ABSENT}}` 表示「原本不存在」 |
| `Repair` | 核心逻辑：`RemoveProvider`（纯函数，可单测）、注册表/环境变量读写、`NormalizeIseDs`（路径归位）、`Apply`、`Rollback` |
| `Shortcut` | 通过 `WScript.Shell` 晚绑定创建/读取 `.lnk` |
| `Launch` | 生成临时 bat → `call settings64.bat` → `start "" ise.exe`，隐藏窗口 |
| `SetupForm` | 向导界面 |
| `SelfTest` | 12 项断言，`AttachConsole` 输出到调用方控制台 |

### 为什么启动器要生成临时 bat

`settings64.bat <ise.exe>` 末尾是 `start %*`，当 ISE 路径含空格时会被 `start` 当成窗口标题而失败。
启动器改为生成一个只有三行的临时 bat（`call settings64.bat` + `start "" "<ise.exe>"`），对花式路径都安全，
`cmd` 退出后立即删除。

---

## 4. 已知限制

- 只处理上面两处已知故障。ISE 14.7 还有别的老环境问题（如 4K/DPI、旧 USB 下载线驱动）不在范围内。
- 修改的是**机器级**设置，因此需要管理员权限；同机多用户共享。
- 环境变量改动对已启动的进程无效，极端情况下需注销/重启。
- `--selftest` 只覆盖纯逻辑（字符串/路径/journal），不会改动系统。

## 5. 排错

| 现象 | 处理 |
|---|---|
| 双击 ISE 快捷方式仍无窗口 | 先确认没有残留的 `ise.exe` 卡住（任务管理器结束它）；再看 `%ProgramData%\ISETOOLS\isetools.log` |
| 提示需要管理员权限 | 向导里的「应用修复并安装」/「回滚」必须提权；从开始菜单启动可避免 |
| 想恢复 WSL 的 `\\wsl.localhost` | 运行一次 `--rollback` 即可把 `P9NP` 加回网络提供程序顺序 |
| ISE 提示找不到许可证 | 检查 `XILINXD_LICENSE_FILE` 指向的文件是否还在；许可证挪位置后要重新运行一次向导 |
