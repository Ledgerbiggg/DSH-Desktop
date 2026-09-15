# Dsh - DeepSeek 桌面客户端
# .NET 9 + WPF + Prism.Unity + WPF-UI 4.0 + WebView2
#
# 常用:
#   make dev     - 开发启动（热部署）：监听源码变化，自动重建并重启（按 Q 退出）
#                  四仓库统一命令：ledger/AiMux/CryptoWidget/Dsh 均为 make dev，无需分别记忆
#   make start   - 开发运行：杀进程 + 构建(Debug) + 运行（不监听，原 make dev 的行为）
#   make build   - 构建解决方案
#   make dist    - 本地打包安装包（需 Inno Setup）
#   make release - 发布（云端出包）：升版本 + 写 notes + 提交 + 推送
#   make config  - 打开配置目录 %AppData%\Dsh
#   make logs    - 打开日志目录

SLN     := Dsh.sln
PRJ     := Dsh\Dsh.csproj
APP_NAME := Dsh.exe
OUT_DIR  := Dsh\bin\Debug\net9.0-windows
APP_PATH := $(OUT_DIR)\$(APP_NAME)
CONFIG   ?= Debug
ISCC     ?= "D:\Inno Setup 7\ISCC.exe"
NOTES    ?= "minor fixes"

# 默认目标：热部署（直接 make 即进入监听重建模式，修改代码自动重启）
.DEFAULT_GOAL := dev

.PHONY: kill
kill:
	@echo "[kill] 清理残留 Dsh 进程..."
	@taskkill /F /IM $(APP_NAME) 2>nul || echo "(无运行实例)"

.PHONY: build
build: kill
	@echo "[build] 构建 $(SLN) (Config=$(CONFIG))..."
	dotnet build $(SLN) -c $(CONFIG) --nologo
	@echo "[build] 完成"

.PHONY: run
run:
	@echo "[run] 启动 $(APP_PATH)..."
	@if exist "$(APP_PATH)" ( \
		start "" "$(APP_PATH)"; \
	) else ( \
		echo "[run] 未找到 $(APP_PATH)，请先执行: make build"; \
	)

# 统一开发启动（热部署）：与 ledger-service 对齐，四个仓库统一敲 make dev 即启动开发
.PHONY: dev
dev:
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev_watch.ps1

# watch 为 dev 的别名，兼容旧习惯
.PHONY: watch
watch: dev

# 一键运行（不监听）：杀进程 → 构建 → 运行
.PHONY: start
start: build run
	@echo "[start] 已启动 Dsh"

.PHONY: publish
publish:
	@echo "[publish] 自包含发布到 _publish/ (win-x64)..."
	dotnet publish $(PRJ) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o _publish

.PHONY: dist
dist: publish
	@echo "[dist] Inno Setup 打包安装包..."
	@if exist "$(ISCC)" ( \
		"$(ISCC)" /DMyAppVersion=$$(powershell -NoProfile -Command "[xml]$$p=Get-Content $(PRJ); $$p.Project.PropertyGroup.Version") installer.iss; \
	) else ( \
		echo "[dist] 未找到 ISCC，请修改 Makefile 中的 ISCC 路径"; \
	)
	@echo "[dist] 安装包已生成 package/Dsh-Setup-*.exe"

.PHONY: release
release:
	@echo "[release] 发布：升版本 + 写 notes + 提交 + 推送..."
	@powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Notes $(NOTES)
	@echo "[release] GitHub Actions 将自动构建并发布安装包"

.PHONY: config
config:
	@echo "[config] 打开配置目录..."
	@explorer "%APPDATA%\Dsh"

.PHONY: logs
logs:
	@echo "[logs] 打开日志目录..."
	@explorer "%APPDATA%\Dsh\logs"

.PHONY: help
help:
	@echo "Dsh - DeepSeek 桌面客户端"
	@echo.
	@echo "默认目标: make = make dev（热部署，Q 退出）"
	@echo.
	@echo "可用目标:"
	@echo "  dev     - 热部署：监听源码，修改自动重建重启（按 Q 退出）"
	@echo "  watch   - dev 的别名"
	@echo "  start   - 一键运行：杀进程 + 构建 + 运行（不监听）"
	@echo "  build   - 构建解决方案（Debug）"
	@echo "  run     - 启动主程序（需先 build）"
	@echo "  kill    - 杀掉残留 Dsh 进程"
	@echo "  dist    - 打包安装包（需 Inno Setup）"
	@echo "  release - 发布（云端出包）：升版本 + 写 notes + 提交 + 推送"
	@echo "  config  - 打开 %AppData%\Dsh 配置目录"
	@echo "  logs    - 打开日志目录"
