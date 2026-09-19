# TailscaleSwitcher

Консольное приложение для Windows — весь интернет-трафик пускает через другой комп в Tailnet (один внешний IP). Интерактивное меню + self-update как в **TerminalV**.

> Портировано с TerminalV: установка через `install.ps1`, публикация через `scripts/publish.ps1`, обновление через GitHub Releases с SHA-256 и атомарной заменой exe.

## Установка

В PowerShell (без прав администратора):

```powershell
irm https://raw.githubusercontent.com/XYphrodite/TailscaleSwitcher/main/install.ps1 | iex
```

Другой каталог или версия:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TailscaleSwitcher/main/install.ps1))) -InstallDir 'D:\TailscaleSwitcher' -Version v0.1.0
```

Ярлык в меню «Пуск» создаётся по умолчанию, на рабочий стол — опционально:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TailscaleSwitcher/main/install.ps1))) -DesktopShortcut
```

Параметры: `-InstallDir` (по умолчанию `%LOCALAPPDATA%\Programs\TailscaleSwitcher`), `-Version` (`latest`), `-NoPath`, `-DesktopShortcut`, `-NoShortcut`.

Ручная установка: скачай `TailscaleSwitcher-win-x64.zip` со [страницы релизов](https://github.com/XYphrodite/TailscaleSwitcher/releases) — self-contained, .NET ставить не нужно. Распакуй и запускай `TailscaleSwitcher.exe`.

После установки:

```powershell
TailscaleSwitcher
```

## Использование

```
1) Показать статус (tailscale status)
2) Показать доступные exit-node
3) Подключиться через exit-node
4) Отключиться от exit-node
5) Проверить внешний IP
6) Показать мой Tailscale IP
7) Открыть админку Tailnet
```

Чтобы собрать пул exit-node:

1. На компе-шлюзе: `tailscale up --advertise-exit-node`
2. Подтверди в админке https://login.tailscale.com/admin/machines → `Edit route settings` → `Use as exit node`
3. На клиенте в TailscaleSwitcher выбери пункт 2/3.

Для переключения нужен запуск **от имени Администратора** (`tailscale set` требует прав).

CLI:

```powershell
TailscaleSwitcher --help          # справка
TailscaleSwitcher --version       # версия (из InformationalVersion)
TailscaleSwitcher update --check  # проверить обновление
TailscaleSwitcher update          # скачать и установить из GitHub Releases
```

## Обновление

Как в TerminalV — без REST API, через публичные URL `https://github.com/XYphrodite/TailscaleSwitcher/releases/download/vX.Y.Z/...` (лимит 60 req/h не касается).

Поток `TerminalV → TailscaleSwitcher`:

| Шаг | TerminalV | TailscaleSwitcher (упрощено) |
|-----|-----------|------------------------------|
| Репозиторий | `XYphrodite/TerminalV` | `XYphrodite/TailscaleSwitcher` |
| Ассет | `TerminalV-win-x64.zip` + `.sha256` | `TailscaleSwitcher-win-x64.zip` + `.sha256` |
| Проверка | SHA-256 из `.sha256` + наличие `exe`/`com`/`wwwroot/index.html` + `exe --help` до и после | SHA-256 + наличие `TailscaleSwitcher.exe` + `--help` проба |
| Замена | `ExecutableReplacer` → `.old-<timestamp>` + pending-маркер `.terminalv-pending-ui` → `PendingUpdateApplier` применяет `wwwroot`/`com` при следующем старте | Прямая атомарная замена `.old-<timestamp>`, без pending (нет `wwwroot`), откат при неудачной пробе |
| Защита | `IsRegularTree` (junction/symlink), `IsOwnedStaging`, маркер только `<install>/.terminalv-update-<GUID>/payload` | Тот же `IsRegularTree`, staging `.tailscaleswitcher-update-<GUID>` |
| Очистка | `RemoveRetiredCopies` + `wwwroot.old-*` + staging | `RemoveRetiredCopies` + staging |
| CLI | `TerminalV update [--check]` | `TailscaleSwitcher update [--check]` |
| Ярлыки | WSH `WScript.Shell`, `SHChangeNotify`, проверка таргета | То же, цель `TailscaleSwitcher.exe`, описание `Tailscale exit-node switcher` |
| Установщик | `install.ps1` ASCII-only, `HttpWebRequest` streaming + `Write-Progress` throttling (PS 5.1 `Invoke-WebRequest` тормозит), TLS 1.2, PATH, Start Menu default | Идентично, `User-Agent: tailscaleswitcher-installer`, очистка каталога полная (без исключения `WebView2`) |

Ошибки сети/rate-limit обрабатываются как в `GitHubReleaseSource.DescribeFailure` — показывает `X-RateLimit-Remaining`/`Reset`.

## Сборка из исходников

Нужен .NET 8 SDK.

```powershell
dotnet build
dotnet run --project src/TailscaleSwitcher -- --help
```

Релизный zip:

```powershell
.\scripts\publish.ps1
# или в другой каталог:
.\scripts\publish.ps1 -OutputDirectory artifacts\candidate-01
```

Результат: `artifacts/TailscaleSwitcher-win-x64.zip` + `.sha256` (артефакты не перезаписываются).

## Структура

```
TailscaleSwitcher/
├── install.ps1                  # как в TerminalV, ASCII-only
├── scripts/publish.ps1          # dotnet publish win-x64 single-file → zip+sha
├── src/TailscaleSwitcher/
│   ├── Program.cs               # интерактивное меню + CLI update
│   └── Update/                  # GitHubReleaseSource, SelfUpdateService, ExecutableReplacer, AppVersion…
├── TailscaleSwitcher.slnx
└── README.md
```

## Лицензия

MIT
