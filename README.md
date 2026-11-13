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
8) Авто-фикс Happ + exit-node (r1600 -> Happ -> xeon)
9) Включить прокси через Xeon (Happ, без exit-node)
10) Выключить прокси через Xeon (прямой выход)
```

Чтобы собрать пул exit-node:

1. На компе-шлюзе: `tailscale up --advertise-exit-node`
2. Подтверди в админке https://login.tailscale.com/admin/machines → `Edit route settings` → `Use as exit node`
3. На клиенте в TailscaleSwitcher выбери пункт 2/3.

Для переключения `exit-node` нужен запуск **от имени Администратора** (`tailscale set` требует прав). Переключение системного прокси через Xeon (`proxy on/off`) работает **без прав Администратора**.

CLI:

```powershell
TailscaleSwitcher --help          # справка
TailscaleSwitcher --version       # версия (из InformationalVersion)
TailscaleSwitcher proxy           # статус системного прокси, доступность Xeon и внешний IP
TailscaleSwitcher proxy on        # включить прокси через Xeon (192.168.1.5:10809 -> fallback 100.119.48.15:10809)
TailscaleSwitcher proxy off       # выключить системный прокси (прямой выход)
TailscaleSwitcher proxy toggle    # переключить прокси вкл/выкл (удобно вешать на ярлык)
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

## Маршрутизация приложений и адресов через другой ПК (`route` / `relay`)

Пускает через клубный ПК только выбранные приложения (`chrome.exe`, путь к `.exe`) и адреса (домен с поддоменами, `full:host`, IP, CIDR). Остальной трафик идёт как обычно. Под капотом — [sing-box](https://github.com/SagerNet/sing-box) (скачивается при первом запуске, версия и SHA-256 зафиксированы в коде) + wintun.

```
moscow_enjoyer:  приложение -> sing-box TUN -> SOCKS5 (Tailscale, логин/пароль) ->
клубный ПК:      relay (sing-box на Tailscale-IP) -> Happ HTTP-прокси 127.0.0.1:10809 -> интернет
```

**Клубный ПК (один раз):**

```powershell
TailscaleSwitcher relay init          # генерирует пароль, печатает команду для клиента
TailscaleSwitcher relay firewall      # от Администратора: TCP-порт только из 100.64.0.0/10
TailscaleSwitcher relay start         # от Администратора; sing-box стартует через Планировщик заданий (SYSTEM) и переживает закрытие терминала/ssh
TailscaleSwitcher relay start --persist   # то же + автозапуск при загрузке Windows
TailscaleSwitcher relay stop | status | uninstall
```

**moscow_enjoyer (от Администратора):**

```powershell
TailscaleSwitcher route profile add club --node 100.89.154.125 --port 1081 --user club --pass <пароль>
TailscaleSwitcher route app add chrome.exe
TailscaleSwitcher route host add example.com      # *.example.com, full:api.example.com, 1.2.3.4, 10.0.0.0/8
TailscaleSwitcher route on                        # повторный вызов применяет изменения
TailscaleSwitcher route off
```

Профилей может быть несколько (`route profile add/remove/use`, `route app|host add --profile <имя>`); приложения и адреса объединяются по «ИЛИ». DNS для доменов из списка резолвится через relay. Конфиги: `%APPDATA%\TailscaleSwitcher\routing.json`, логи и config sing-box: `%APPDATA%\TailscaleSwitcher\run\`. То же доступно в меню, пункт 11.

Ограничения: relay отдаёт в Happ только TCP (UDP не проксируется); если на клиенте включён Happ в режиме TUN, возможны конфликты маршрутов — клиентская часть требует проверки на живой машине.

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
