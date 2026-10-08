# HANDOFF: VpnProvisioner — промпт для продолжения работы

Скопируй этот файл целиком и вставь в новый чат как первый промпт.

---

## Роль

Ты продолжаешь разработку Windows-приложения **VpnProvisioner**. Проект полностью рабочий, протестирован E2E на реальном VPS. UI переработан на Fluent Design (WPF-UI 4.3.0). Твоя задача — развить его дальше. Не переизобретай то, что уже проверено: сначала прочитай исходники и этот контекст.

## Что это за приложение

Минималистичное WPF-приложение (.NET 8) для автоматической установки и настройки VPN на купленном VPS по SSH. Пользователь вводит IP сервера, порт SSH, пользователя и пароль (или путь к SSH-ключу) → выбирает протокол → нажимает «Установить» → приложение подключается, скачивает/устанавливает серверное ПО, генерирует ключи, пишет конфиг, настраивает systemd/фаервол/BBR и выдаёт готовую ссылку/конфиг клиента.

**Поддерживаемые протоколы:** Xray VLESS/Reality, Hysteria 2, Trojan, AmneziaWG.

## Расположение

- Проект: `C:\Users\sreda\VpnProvisioner`
- Готовый exe (single-file, framework-dependent): `C:\Users\sreda\VpnProvisioner\publish\VpnProvisioner.exe` (~12 МБ + binaries/)
- Тестовый harness: `C:\Users\sreda\VpnProvisioner\tests\Harness`
- Данные тестового сервера: `C:\Users\sreda\Documents\server\server-valley.txt` (IP 87.251.79.20, root + пароль; ключевой доступ настроен)
- Тестовый SSH-ключ: `%TEMP%\opencode\vpnprov-test\id_ed25519`
- Автогенерируемые ключи: `%APPDATA%\VpnProvisioner\keys\<host>_<port>_id_ed25519`

## Структура кода

```
VpnProvisioner/
  VpnProvisioner.csproj          # net8.0-windows, WPF, SSH.NET 2026.0.0, WPF-UI 4.3.0
  App.xaml / App.xaml.cs         # ThemesDictionary (Light default), ControlsDictionary, системная тема из реестра
  MainWindow.xaml(.cs)           # FluentWindow + Mica + TitleBar, карточный layout, локализация RU/EN
  Localization.cs                # Статический класс T(key) → RU/EN строки, DetectSystemLanguage()
  Core/
    ProvisionRequest.cs          # Host, SshPort, SshUser, KeyPath(nullable), KeyPassphrase, Password(nullable), Sni, ForceReconfigure, Protocol
    ServerInfo.cs                # OsName, Arch, DistroId, HasXray, Port443Busy, Port443Details; PackageManager; XrayAsset
    ProvisionResult.cs           # Link, Uuid, PublicKey, ShortId, Sni, Port
    ProvisionException.cs
  Ssh/
    SshSession.cs                # SSH.NET: Connect (key/password), password→key upgrade, RunAsync, UploadTextAsync, UploadBinaryFileAsync (SFTP), retry 3x
    ServerProber.cs              # uname/os-release/which xray/ss -tlnp
    KnownHostsStore.cs           # %APPDATA%\VpnProvisioner\known_hosts.json (SHA256-отпечатки, MITM-защита)
    ExecResult.cs
  Installers/
    IProtocolInstaller.cs        # Id, DisplayName, ProvisionAsync(ssh, info, req, log, ct) → ProvisionResult
    ProtocolRegistry.cs          # XrayReality, Hysteria2, Trojan, AmneziaWg
    XrayRealityInstaller.cs      # Xray VLESS/Reality: GitHub check → download or local SFTP upload
    Hysteria2Installer.cs        # Hysteria 2: YAML config, self-signed EC cert, QUIC UDP+TCP 443
    TrojanInstaller.cs           # Trojan: JSON config, self-signed EC cert, TCP 443
    AmneziaWgInstaller.cs        # AmneziaWG: PPA .deb download → dpkg install → DKMS → wg-quick config
  binaries/
    Xray-linux-64.zip            # ~20 MB
    Xray-linux-arm64-v8a.zip     # ~19 MB
    Xray-linux-arm32-v7a.zip     # ~19 MB
    Xray-linux-32.zip            # ~19 MB
    hysteria/
      hysteria-linux-amd64       # ~23 MB (single binary)
      hysteria-linux-arm64       # ~21 MB
    trojan/
      trojan-1.16.0-linux-amd64.tar.xz  # ~1.4 MB
    amneziawg/                   # .deb файлы для офлайн-установки (пока пусто — скачиваются с PPA)
tests/Harness/                   # install-key | cleanup | provision <host> <port> <user> <key_or_password> [sni] [--force] [--protocol <id>]
```

## UI (текущее состояние)

- **FluentWindow** с Mica backdrop, скруглёнными углами, кастомный TitleBar
- **Тема**: определяется из реестра (`AppsUseLightTheme`), переключается кнопкой (Dark/Light)
- **Язык**: определяется из `CurrentUICulture` (ru→RU, иначе EN), переключается кнопкой Globe
- **Кнопки темы/языка**: правый верхний угол контента, ToolTip с `Placement="Bottom"`
- **Карточки** (`ui:Card`): Подключение к серверу → Протокол и параметры → Кнопка «Установить» → Лог → Результат
- **Поля ввода**: `ui:TextBox` с PlaceholderText + Icon, `ui:PasswordBox` с RevealButtonEnabled
- **Лог**: Border с CornerRadius=8, BorderBrush, MinHeight=200, MaxHeight=320, шрифт Cascadia Code
- **SNI**: деактивируется (IsEnabled=false, Opacity=0.4) при выборе протокола ≠ Xray
- **Результат**: карточка появляется только после успешной установки

## Ключевые технические факты (проверено, не наступай повторно)

1. **SSH.NET 2026.0.0**: `SshClient.CreateCommand(cmd)` → `SshCommand` (Result, Error, ExitStatus nullable int, CommandTimeout). `HostKeyReceived` → `FingerPrintSHA256`. `SftpClient` для бинарной загрузки файлов (`UploadFile(Stream, path)`). `SftpClient` НЕ имеет RunCommand.

2. **Password → Key upgrade**: если указан пароль (без ключа) → генерация ed25519 через `ssh-keygen` (Windows 10+ встроен) → подключение по паролю → установка pub key в authorized_keys → disconnect → reconnect по ключу. Ключи в `%APPDATA%\VpnProvisioner\keys\`.

3. **Xray**: `github.com/XTLS/Xray-core`, ассеты `Xray-linux-64.zip` и т.д. URL: `releases/latest/download/<asset>`. Новый вывод `xray x25519`: `PrivateKey:` / `Password (PublicKey):`.

4. **Reality dest**: `www.microsoft.com:443` ЛОМАЕТ Reality. `www.google.com:443` работает. Дефолт SNI = `www.google.com`.

5. **Права конфига Xray**: `root:xray` `640`, иначе status=23.

6. **Go CLI flags**: `-config` парсится как `-c onfig` (Go pflag). Для Hysteria и Trojan использовать `-c <path>`, НЕ `-config <path>`.

7. **Hysteria 2 v2.13+**: config YAML требует `auth: {type: password, password: <pass>}` (map, не string). Masquerade proxy: `url: https://<domain>` (не `addr:`). Repo: `HyNetworks/hysteria` (redirect с `apernet/hysteria`).

8. **Trojan v1.16.0**: config JSON требует `"users": ["<pass>"]` внутри основного конфига. Флаг: `-c <path>`. Релиз 2020, только amd64 tar.xz. URL: `github.com/trojan-gfw/trojan/releases/download/v1.16.0/trojan-1.16.0-linux-amd64.tar.xz`.

9. **AmneziaWG PPA**: `https://ppa.launchpadcontent.net/amnezia/ppa/ubuntu` (owner `amnezia`, НЕ `amnezia-vpn`). Пакеты: `amneziawg-dkms` (all), `amneziawg-tools` (amd64), `amneziawg` (all). Скачивать .deb напрямую из `pool/main/a/...` и ставить через `dpkg -i`. DKMS-компиляция на слабом VPS занимает 5-15 минут. PostUp/PostDown: НЕ использовать `ip6tables` с IPv4-подсеткой (падает). Сервис: `awg-quick@awg0`. Конфиг: `/etc/amnezia/amneziawg/awg0.conf`.

10. **GitHub accessibility check**: `curl -sI --max-time 10 -o /dev/null -w '%{http_code}' https://github.com` → 200/301/302 = доступен. Для AmneziaWG: проверка `ppa.launchpadcontent.net/amnezia/ppa/ubuntu/dists/noble/Release`.

11. **SFTP upload**: `SftpClient(ConnectionInfo)` → `UploadFile(FileStream, remotePath)`. Создаётся отдельное соединение. Используется для загрузки бинарников на сервер при офлайн-установке.

12. **sudo**: если `id -u` != 0, команды получают префикс `sudo `. Сложные shell-цепочки не используются.

13. **Безопасность**: UUID/X25519 генерируются НА СЕРВЕРЕ. Отпечатки в known_hosts. Не логировать пароли/приватные ключи.

14. **csproj**: `<Compile Remove="tests/**"/>` обязателен (CS0579). `<Content Include="binaries/**" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest"/>`.

15. **Формат ссылок**:
    - Xray: `vless://<uuid>@<host>:443?encryption=none&security=reality&type=tcp&fp=chrome&sni=<sni>&pbk=<b64url>&sid=<8hex>#<host>`
    - Hysteria2: `hysteria2://<pass>@<host>:443?sni=<sni>&insecure=1#<host>`
    - Trojan: `trojan://<pass>@<host>:443?sni=<sni>#<host>`
    - AmneziaWG: WireGuard client .conf (текст с `[Interface]` / `[Peer]`, Jc/Jmin/Jmax)

16. **WPF-UI 4.3.0**: namespace `xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`. `ui:FluentWindow` extends `System.Windows.Window`. `ui:PasswordBox` extends `Wpf.Ui.Controls.TextBox` (имеет `.Password` string property). `SymbolRegular` enum для иконок. `ApplicationThemeManager.Apply(theme)` для смены темы. `LayerFillColorDefaultBrush` — brush-ресурс (НЕ `ApplicationBackgroundColor` — это Color, не Brush!). `TitleBar` имеет `TrailingContent` (НЕ `.Content`).

## Команды

```powershell
# сборка
dotnet build -c Release                                   # workdir: C:\Users\sreda\VpnProvisioner
# публикация single-file portable
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
# harness
dotnet build tests\Harness -c Release
# provision (ключ):
& tests\Harness\bin\Release\net8.0-windows\Harness.exe provision 87.251.79.20 22 root "$env:TEMP\opencode\vpnprov-test\id_ed25519" www.google.com --force
# provision (пароль):
& tests\Harness\bin\Release\net8.0-windows\Harness.exe provision 87.251.79.20 22 root "D2wn31kI2Doi9FS8" www.google.com --force
# provision (другой протокол):
& tests\Harness\bin\Release\net8.0-windows\Harness.exe provision 87.251.79.20 22 root "$env:TEMP\opencode\vpnprov-test\id_ed25519" www.google.com --protocol hysteria2
& tests\Harness\bin\Release\net8.0-windows\Harness.exe provision 87.251.79.20 22 root "$env:TEMP\opencode\vpnprov-test\id_ed25519" www.google.com --protocol trojan
& tests\Harness\bin\Release\net8.0-windows\Harness.exe provision 87.251.79.20 22 root "$env:TEMP\opencode\vpnprov-test\id_ed25519" www.google.com --protocol amneziawg
# cleanup:
& tests\Harness\bin\Release\net8.0-windows\Harness.exe cleanup 87.251.79.20 22 root "$env:TEMP\opencode\vpnprov-test\id_ed25519"
```

## Методика E2E-теста

1. Cleanup: остановить/удалить все сервисы на сервере (`systemctl stop xray hysteria-server trojan awg-quick@awg0`, удалить конфиги/бинарники).
2. `Harness.exe provision <host> 22 root <key_or_password> www.google.com --protocol <id> [--force]` → получить ссылку.
3. Проверка: `systemctl is-active <service>`, `ss -tlnp | grep 443` или `awg show awg0`.
4. Для Xray: локальный socks5-клиент + `curl -x socks5h://127.0.0.1:10800 https://ifconfig.me` → IP должен смениться.
5. Для Hysteria2: клиент `hysteria2://...` в Shadowrocket/v2rayN/Amnezia.
6. Для Trojan: импорт ссылки в v2rayN/Shadowrocket.
7. Для AmneziaWG: импорт .conf в AmneziaWG-клиент (Windows/Android/iOS).

## Текущее состояние

- Все 4 протокола работают E2E на тестовом сервере (valley-vps, 87.251.79.20, Ubuntu 24.04 x86_64).
- Password auth + auto key generation работает.
- GitHub check + local binary fallback работает (Xray, Hysteria, Trojan).
- AmneziaWG: online (PPA .deb download) работает; offline (локальные .deb в binaries/amneziawg/) требует размещения файлов.
- UI: Fluent Design (WPF-UI 4.3.0), Mica backdrop, карточный layout, Fluent Icons.
- Тема: автоопределение из реестра + переключатель Dark/Light.
- Язык: автоопределение из CurrentUICulture + переключатель RU/EN.
- SNI: деактивируется для не-Xray протоколов.
- Publish: `binaries/` копируется в publish-папку. Portable single-file exe ~12 МБ.

## Следующие шаги (текущая задача)

1. **Подписать приложение именем автора** — добавить AssemblyInfo (Company, Product, Copyright), иконку приложения (.ico), возможно Authenticode-подпись.
2. **Создать installer-версию** — Inno Setup скрипт (`.iss`): установка в Program Files, ярлык на рабочем столе/в меню Пуск, uninstaller. Включить `binaries/` в установщик.
3. **Portable-версия** — уже есть (`publish/VpnProvisioner.exe` + `binaries/`). Упаковать в .zip для скачивания.
4. **Опубликовать на GitHub** — создать репозиторий, README (скриншоты, описание, инструкции), Release с двумя артефактами: installer (.exe/.msi) и portable (.zip).

## Дорожная карта (будущие доработки)

1. **Офлайн-бандлы AmneziaWG**: скачать .deb пакеты в `binaries/amneziawg/` для полностью автономной установки.
2. **Режимы удаления/обновления**: полная очистка для каждого протокола + обновление бинарника без пересоздания ключей.
3. **Несколько клиентов на одном сервере**: добавление uuid/password в существующий конфиг.
4. **CLI-режим** приложения (AttachConsole + аргументы) для автоматизации.
5. **self-contained publish** (~150 МБ) для запуска без установленного .NET Runtime.
6. **Улучшения UX**: кнопка «Отмена», прогресс загрузки, сохранение лога в файл, проверка доступности порта снаружи.
7. **Новые протоколы**: Shadowsocks-202R, TUIC, AnyTLS.
8. **Профили серверов**: сохранение списка (IP, порт, протокол) в JSON.
9. **Автоматическая проверка работоспособности** после установки (probe tunnel).
10. **Обновление бинарников**: проверка версии Xray/Hysteria при каждом запуске, предложение обновить.

## Правила

- Не выводить и не логировать пароли/приватные ключи.
- Не менять дефолтный SNI на microsoft (факт 4).
- После изменений: `dotnet build -c Release` без ошибок/предупреждений.
- При изменении установщика — прогонять E2E на тестовом сервере.
- Стиль кода: file-scoped namespaces, nullable enable, implicit usings, комментарии не добавлять без запроса.
- Go-утилиты (hysteria, trojan): флаг конфига `-c`, НЕ `-config`.
- WPF-UI: использовать `LayerFillColorDefaultBrush` (Brush), НЕ `ApplicationBackgroundColor` (Color). `TitleBar.TrailingContent`, НЕ `.Content`.
