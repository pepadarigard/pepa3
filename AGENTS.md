# AGENTS.md — как менять Pepa и ничего не сломать

Этот файл — для разработчика и для ИИ-агента (Codex, Cursor, Claude Code и др.), который меняет репозиторий. В нём
правила, которые нельзя нарушать, карта проекта, команды, соглашения, рецепты «как добавить» и подводные камни.
Подробное устройство описано в `docs/ARCHITECTURE.md`.

**Язык.** Владелец проекта пишет по-русски. Отвечайте ему по-русски и простыми словами: он не обязан знать C# или React.
На русском пишутся интерфейс, сообщения для пользователя, комментарии и XML-документация в коде, тексты в `docs/` и
сообщения коммитов. Имена в коде — на английском: типы, методы, переменные, имена тестов.

## 1. Что такое Pepa

Pepa — локальная платформа автоматизаций для Windows. Сценарий собирается из блоков в визуальном конструкторе. Его
можно также записать (действия в браузере или в окнах Windows) или получить от ИИ-помощника по описанию. Движок
исполняет сценарий как долгий надёжный процесс: ожидания, события, ресурсы с очередью, расписания, параллельные ветви,
вызов одного сценария из другого. С внешним миром Pepa работает через адаптеры: файлы, таблицы Excel и CSV, HTTP, почта,
браузер (Playwright) и профили AdsPower, Telegram (собственный аккаунт пользователя), окна Windows, мышь и клавиатура.
Главное свойство — надёжность: каждое действие записывается на диск до того, как оно выполнено; после сбоя ничего не
продолжается само; действие с неизвестным итогом не повторяется вслепую. Стек: .NET 10 (движок, SQLite, локальный HTTP
API на 127.0.0.1) и React 19 (интерфейс в WebView2 внутри окна WPF или в обычном браузере при `pepa-server`).

Документы:
- `README.md` — для пользователя: как запустить, первые шаги, где лежат данные.
- `docs/PRODUCT_VISION.md` — назначение и принципы продукта (раздел «Принципы» обязателен к прочтению).
- `docs/ARCHITECTURE.md` — устройство: проекты, исполнение шага, восстановление, хранилище, API.
- `docs/DECISIONS.md` — решения D-001…D-090 и следующие, с причинами. Перед изменением поведения найдите своё D-NNN.
- `docs/TESTING.md` — стратегия тестирования и карта тестов: какое требование каким тестом проверено.
- `docs/CHECKLIST.md` — что проверить вручную на настоящем ПК (в разработке это проверено только на имитациях).
- `docs/ROADMAP.md` — этапы и что готово. `docs/AUDIT.md` — отчёт аудита: что найдено, что исправлено, что осталось.
- `samples/Pepa.Plugins.Sample/README.md` — как написать расширение со своими блоками.

## 2. Золотые правила надёжности

Эти правила — сам продукт. Изменение, которое нарушает любое из них, — ошибка, даже если все тесты зелёные. Если
задачу нельзя решить без нарушения правила, остановитесь и спросите владельца. Источники правил: `PRODUCT_VISION.md`
(«Принципы»), `DECISIONS.md` (D-006, D-010, D-017, D-022, D-023, D-032, D-055, D-064, D-065) и инварианты в коде.

### 2.1. Никаких незапланированных действий
- Движок делает только то, что задано в схеме. Никаких повторов «на всякий случай», обходных путей и автоисправлений.
- `NodeTypeDescriptor.Effect` должен быть честным. `None` — никакого ввода-вывода: такой блок выполняется без записи
  «выполняется» и с `CancellationToken.None`, поэтому настоящий внешний эффект в нём повторится после сбоя. `ReadOnly` —
  только читает (его `Unknown` становится ошибкой). `External` — меняет мир и проходит путь `Planned → Running → итог`.
- Автоповтор (`NodeSettings.RetryCount`) — только для `NodeResult.Failure` с `Retryable = true` и при `Effect != None`.
  У `NodeResult.Fail(msg)` по умолчанию `retryable: true`: для окончательного отказа передавайте `retryable: false`.
- Перед любым стартом сценарий проходит `ScenarioValidator`: обычный запуск (`PrepareStartAsync`), дочерний процесс,
  автозапуск. Черновик с ошибками сохранить можно: `ScenarioService` при сохранении не проверяет.
- Запущенный процесс исполняет свой снимок `ProcessRecord.DefinitionJson`, правка сценария на него не влияет.
  Единственное исключение — явная команда «Взять блок из сценария» (`ResumeAction.RetryWithNewBlock`, D-085).
- ИИ-помощник (`src/Pepa.Assistant`) ничего не запускает. Его черновик проходит тот же `ScenarioValidator`, и
  пользователь вставляет его в схему сам.

### 2.2. Неожиданное — это остановка и вопрос человеку
- Неясное не угадывается (D-017), а останавливает процесс: событие одинаково подходит нескольким или условие
  сопоставления упало → `EventState.Conflict`; в «Выбор варианта» подошло несколько вариантов → ошибка без повтора;
  взаимная блокировка ресурсов → отказ (`WouldDeadlockAsync`); селектор нашёл несколько элементов → ошибка, а не «первый
  попавшийся»; ключ или фильтр задан, но после подстановки пуст → `NodeInputException`, а не «подходит всё».
- Ошибка шага без ветки «Ошибка» переводит процесс в `NeedsAttention` (`AttentionKind.StepFailed`), неизвестный итог —
  в `AttentionKind.UnknownStep`. Дальше решает человек через `ProcessEngine.ResumeAsync`, и только действием из
  `GetAllowedActions`. Интерфейс показывает кнопки только из `allowedActions` и просит подтвердить рискованные решения.
- Успех на выход `PortKind.Optional`, у которого нет связи, — ошибка шага, а не тихий конец процесса. Ошибка одного
  процесса не затрагивает другие.

### 2.3. Действие с неизвестным итогом не повторяется само
- Шаг с `Effect != None` записывается как `StepState.Running` вместе с `Checkpoint` из `PrepareAsync` **до** вызова
  `ExecuteAsync` (`ExecuteActionAsync` в `src/Pepa.Engine/ProcessEngine.Execution.cs`). Если после сбоя шаг найден в
  `Running`, он становится `Unknown` и сам больше не выполняется никогда.
- Как блок сообщает итог (`src/Pepa.Core/Nodes/INodeExecutor.cs`):
  - `NodeInputException` — только до любого внешнего эффекта: «точно не выполнялось», доступно «Исправить и повторить».
  - `Failure` — действие точно не случилось. `MaybeExecuted = true`: могло случиться, но повтор доказуемо безопасен
    (`http.send` с `Idempotency-Key`). `ActionRan = true`: действие выполнилось, но процессу некуда идти дальше.
  - `Unknown` — действие могло случиться: обрыв после отправки, тайм-аут, отмена. `Unknown { AlreadyChecked = true }`
    сразу отдаёт решение человеку без автоматической сверки: немедленная сверка дала бы ложное «не сделано» (D-032).
  - `VerifyAsync` только читает и никогда не повторяет действие. `Done` — только при уверенности. `NotDone` — только
    если действие точно не случилось: после него движок может выполнить шаг снова. Иначе — `Inconclusive`.
- Ключи защиты от повторов (Telegram `randomId`, HTTP `Idempotency-Key`, Message-ID письма) одинаковы во всех попытках
  шага: блок берёт их из `context.PreviousCheckpoint`. Если попытка могла выполниться, а следующая окончательно упала,
  итог — `Unknown` (`Output.uncertainStepId`), а не ветка «Ошибка».
- Непосредственно перед каждым внешним эффектом вызывается `cancellationToken.ThrowIfCancellationRequested()`: шаг,
  брошенный по тайм-ауту или остановке, не должен действовать позже.
- Повторить шаг после `Unknown` можно только решением человека: `ResumeAction.Verify`, `Retry`, `MarkSucceeded`,
  `MarkFailed`. `RetryWithChanges` и `RetryWithNewBlock` доступны только после подтверждённой ошибки.

### 2.4. Итог шага — одна транзакция
- Всё, что меняет шаг, записывается одним `IStoreWrite` в `CommitResultAsync`, который вызывается из `ApplyResultAsync`
  (`ProcessEngine.Execution.cs`): строка шага, переменные, память блока, маркер, статус процесса, подписки, очередь и
  владелец ресурса, дочерний процесс, отправленное событие, журнал.
- Действия платформы (ресурсы, события) решаются в той же транзакции — `ResolveIntentAsync` в `ProcessEngine.Platform.cs`.
- Работа, которая сама пишет в базу (возврат событий, отмена дочерних процессов, маршрутизация), выполняется только после
  подтверждения транзакции: её собирает `CommitEffects`, а запускает `ApplyEffectsAsync`.
- Открытая запись во всём приложении одна (`SqliteStore._writeLock`, замок не реентерабельный). Внутри открытой записи
  нельзя вызывать то, что само открывает запись через `BeginWriteAsync` (`PublishEventAsync`, `AddJournalNoteAsync`,
  `CancelAsync` и т. п.): это вечная взаимная блокировка. Порядок замков: сначала маршрутизация (`_routingLock`), потом
  запись. Каждый `IStoreWrite` открывается через `await using`; без `CommitAsync` запись откатывается.
- Процесс меняет только владелец его `ProcessSlot`. Пауза и остановка во время шага применяются на границе шага.

### 2.5. После перезапуска ничего не продолжается само
- `ProcessEngine.StartAsync` вызывает `RecoverAsync` (`ProcessEngine.Recovery.cs`) раньше, чем начинают работать
  исполнители, HTTP и адаптеры: шаги в `Running` становятся `Unknown` (процесс — `NeedsAttention`), шаги в `Planned` —
  `Cancelled`, остальные активные процессы — `Paused(Restart)`.
- Ожидания (таймер, событие, ресурс) — записи в базе, а не потоки: переживают перезапуск, но продолжаются только после
  «Продолжить». Пропущенные запуски по расписанию считаются (`RecordMissedSchedulesAsync`), но не выполняются; события,
  пришедшие до текущего запуска Pepa, не запускают процессы по событию (D-023).
- Если база восстановлена из копии, все незавершённые процессы получают `NeedsAttention` (`RestoredFromBackup`, D-047).
- Адаптеры при старте только переподключаются, слушают и сообщают. Внешнее действие они не повторяют и ничего не
  закрывают: например, `BrowserSessions.Prune` только забывает сессию и пишет об этом в журнал. В интерфейсе нет и не
  должно быть кода «автопродолжения».

### 2.6. События дедуплицируются
- Событие уникально по паре (`source` в нижнем регистре, `external_id`): индекс `ux_events_external` и проверка в
  `SqliteWrite.TryInsertEventAsync`. `ExternalId` входящего события детерминирован и не меняется между перезапусками:
  Telegram — `аккаунт:чат:сообщение[:edit:…]`, почта — `аккаунт:uidValidity:uid`, папки — `watchId:путь:размер:время`.
- Дважды запустить один и тот же процесс нельзя: ключ происхождения `origin_key` уникален (`ux_processes_origin`).
  Форматы: `schedule:<триггер>:<время>`, `event:<триггер>:<событие>`, `child:<родитель>:<шаг>`, `<batchKey>:<i>`.
- Состояния события и подписки меняются только условно, из ожидаемого состояния (`TryUpdateEventAsync`,
  `TrySetSubscriptionStateAsync`); события сопоставляются по одному за раз. Служебные источники `pepa.process` и
  `pepa.question` недоступны снаружи: их нельзя отправить через API или «Отправить событие» и указать в автозапуске.

### 2.7. Один профиль AdsPower — один процесс (ресурсы)
- Монопольные ресурсы имеют фиксированные ключи: `adspower:<profileId>` (`BrowserSessions.AdsPowerResourceKey`),
  `pepa-browser:<профиль>` (`browser.open`), `desktop:input` (`DesktopNodes.InputResource`, мышь и клавиатура). Блок
  объявляет ресурс через `INodeExecutor.RequiredResource`; движок занимает его до действия или ставит процесс в очередь.
- У ресурса не больше одного владельца; приостановленный в очереди сохраняет место, но пропускается; завершившийся
  процесс всегда освобождает свои ресурсы.
- Pepa не захватывает и не закрывает то, что открыла не она: профиль, открытый владельцем, и профиль, перезапущенный
  без Pepa (D-078, D-089).

### 2.8. Секреты никогда не показываются и не пишутся в журнал
- Секретная переменная (`VariableDefinition.Secret`) хранит настоящее значение в данных процесса: оно нужно для работы
  и проверки результата. Скрывается значение при показе (`SecretMasker` в `src/Pepa.Engine/ProcessEngine.Secrets.cs`,
  в API — через `ProcessMapper`) и в любом тексте, который пишет движок: сообщения, ошибки, итоги, снимки, журнал
  (`ProcessEngine.Redact`).
- Маска — `ProcessEngine.SecretMask` («••••»). Секреты переходят вместе с данными в другие процессы и события
  (`ProcessRecord.Secrets`, `EventRecord.Secrets`, D-055). `StepRecord.Input` и `ProcessRecord.Variables` содержат
  настоящие секреты: не пишите их в журнал и не возвращайте из API без `SecretMasker`.
- Учётные данные интеграций — через `Pepa.Core.Security.Secrets.Protect(value, "Pepa.<Назначение>")` (DPAPI текущего
  пользователя). API отдаёт только признак (`HasPassword`, `AdsPowerHasKey`) или подсказку (`AssistantKeyStore.Hint`).
- `ILogger` и журнал приложения (`logs/`) ничего не маскируют. В вызовы `Log*` нельзя передавать пароли, токены,
  значения заголовков, строки запроса URL.
- Снимок экрана при ошибке не делается, если на экране может быть секрет: в браузере это решает `SecretVisibleAsync`,
  в окнах Windows снимок пропускается при любых `SecretValues`. Экспорт сценария очищает значения секретов по умолчанию.
- Эти исключения уже есть — не делайте новых: секреты процессов в `pepa.db` и `backups/` открыты по замыслу; вне Windows
  `Secrets.Protect` ничего не шифрует. Ключ AdsPower в базе зашифрован (`SettingsService.Serialize`), в памяти — открыт.

### 2.9. Ошибки не скрываются
- Пользователь видит понятную причину на русском. API отвечает `ErrorDto { error, validation }`, исключения
  сопоставляются кодам в `ApiEndpoints.Map`: `ScenarioInvalidException` → 422, `EngineOperationException` и
  `ConcurrencyException` → 409, `SettingsValidationException` → 400.
- Любое другое исключение даёт пустой 500, и интерфейс показывает только «Ошибка 500»: ошибки адаптеров ловите явно.
  Недоступная интеграция отказывает громко и с причиной (`UnavailableDesktop`, `TelegramUnavailableException`,
  `EmailService.Problem`), а не молчит и не ждёт вечно.
- Пустой `catch` допустим, только когда сбой действительно ни на что не влияет, и даже тогда с записью в журнал
  приложения. Снимок экрана — только подсказка: если он не получился, итог шага не меняется. Уведомление при сбое не
  повторяется, но сбой записывается в журнал.

### 2.10. Сохранённые имена не переименовываются
Эти имена хранятся в сценариях пользователей (`ScenarioDefinition.SchemaVersion = 1`, миграций конфигурации нет), в
экспорте, примерах, рекордерах и подсказках ИИ-помощнику: тип блока (`file.write`), id выходов (`out`, `error`,
`timeout`…), имена полей и значения `Select`, имена членов enum в базе, форматы ключей (`origin_key`, `ExternalId`).
Новое поле добавляется с `Default`, при котором поведение не меняется. Переименование — только с миграцией и D-NNN.

## 3. Карта репозитория

Проекты `src/`:
- `src/Pepa.Core` — модель сценария (`Model/ScenarioDefinition.cs`), контракт блока (`Nodes/INodeExecutor.cs`,
  `NodeTypeDescriptor.cs`), `NodeRegistry`, проверка сценария (`Validation/ScenarioValidator.cs`, `ParallelCheck.cs`),
  выражения Jint в песочнице, `PepaJson`, контракт расширений `Plugins/IPepaPlugin.cs`, DPAPI (`Security/Secrets.cs`).
- `src/Pepa.Engine` — `ProcessEngine` в partial-файлах по темам (исполнение, события, ресурсы и действия платформы,
  триггеры, восстановление, вопросы, секреты); встроенные блоки логики и потока (`Nodes/`), `ScenarioService`,
  `ScheduleCalculator`, контракт хранилища (`Persistence/IPepaStore.cs`, `Records.cs`).
- `src/Pepa.Storage.Sqlite` — `SqliteStore`: один файл `pepa.db` (WAL, `synchronous=FULL`), миграции
  (`SqliteStore.Schema.cs`), онлайн-копии.
- `src/Pepa.Adapters.FileSystem` — рабочая папка (`Workspace`), блоки `file.*` и `folder.*`, «Дождаться файла»,
  слежение за папками (`FolderWatcher`, источник `files`).
- `src/Pepa.Adapters.Tables` — Excel (.xlsx/.xlsm) и CSV/TSV: `table.read`, `table.find`, `table.append`, `table.update`.
- `src/Pepa.Adapters.Http` — `http.get` (только чтение) и `http.send` (без слепых повторов).
- `src/Pepa.Adapters.Email` — `EmailService`: следит за папкой IMAP только на чтение, письма становятся событиями
  `email`; отправка через SMTP. Блоки `email.wait`, `email.send`.
- `src/Pepa.Adapters.Browser` — Playwright (Edge/Chrome) и AdsPower (`AdsPowerClient`: Local API v2, запасной v1),
  `BrowserSessions`, блоки `browser.*` и `adspower.open`, `browser.waitAny`, запись действий (`BrowserRecorder`).
- `src/Pepa.Adapters.Telegram` — `TelegramService` (WTelegramClient, MTProto, собственный аккаунт): блоки
  `telegram.*`, источник событий `telegram`, прокси.
- `src/Pepa.Adapters.Desktop` — блоки `desktop.*` (окна, мышь, клавиатура) за `IDesktop`; запись действий
  (`DesktopRecorder` за `IDesktopInputMonitor`). Собирается на любой ОС.
- `src/Pepa.Adapters.Desktop.Windows` — `UiaDesktop` (FlaUI UIA3, SendInput), `UiaInputMonitor`. Только `net10.0-windows`.
- `src/Pepa.Assistant` — ИИ-помощник конструктора (Anthropic SDK): `ScenarioAssistant`, `ScenarioPrompt` (системные
  указания и каталог блоков), `DraftConverter`.
- `src/Pepa.Host` — точка сборки приложения (`PepaHost.cs`). API (`Api/ApiEndpoints.cs`, `Dtos.cs`, `ProcessMapper.cs`,
  `EventHub.cs` для SSE), защита доступа (`Security/LocalAccessMiddleware.cs`), настройки и ключ помощника
  (`Settings/`), копии и срок хранения (`Backups/`), уведомления (`Notifications/`), сон компьютера (`Power/`), примеры
  (`Samples/SampleScenarios.cs`), расширения (`Plugins/PluginLoader.cs`), снимки экрана (`Snapshots/`), автозапуск
  (`Startup/`), `InstanceLock`, журнал в файл (`Logging/`).
- `src/Pepa.Server` — `pepa-server`: тот же `PepaHost` без окна, для разработки, CI и e2e.
- `src/Pepa.Desktop` — `Pepa.exe`: оболочка WPF с WebView2, значок в области уведомлений, автозапуск, корректное
  завершение сеанса Windows, мост `pickFolder`/`openFolder`. Только `net10.0-windows`.

Тесты `tests/` (по проекту на модуль):
- `tests/Pepa.Core.Tests` — проверка сценария, выражения, сериализация, сравнение текста.
- `tests/Pepa.Engine.Tests` — движок на настоящем SQLite и управляемом времени; общая инфраструктура в `Infrastructure/`
  (`EngineHarness`, `ScenarioBuilder`, `FaultInjectingStore`, `TestActionNode` вместе с `ExternalWorld`).
- `tests/Pepa.Adapters.*.Tests` — каждый адаптер на имитации своего сервиса и с настоящим движком;
  `tests/Pepa.Assistant.Tests` — настоящий SDK против `FakeAnthropic`.
- `tests/Pepa.Host.Tests` — настоящий `PepaHost` на случайном порту (`HostFixture`): API, настройки, секреты, перезапуск.

Остальное:
- `ui/` — интерфейс (React 19, TypeScript, Vite, TanStack Query, React Flow). В `src/api/` лежат `client.ts`
  (единственное место, где строятся адреса `/api`), `types.ts` (ручная копия `Dtos.cs`), `hooks.ts` и `events.ts` (SSE).
  Далее `src/pages/` (страницы), `src/pages/editor/` (конструктор), `src/components/`, `src/lib/` (логика и тесты
  `*.test.ts`), `src/i18n/ru.ts`, `src/styles/app.css`, `e2e/smoke.cjs` (сквозная проверка), `public/`.
- `samples/Pepa.Plugins.Sample` — образец расширения (`sample.textcase`, `sample.appendline`); тесты хоста используют
  его сборку.
- `build/build.ps1` — сборка на Windows. `.github/workflows/build.yml` — CI. `docs/` — документы (раздел 1).
- Корень: `global.json` (SDK 10.0.100, `rollForward: latestFeature`), `Directory.Build.props`, `Directory.Build.targets`
  (`PlaywrightPlatform`), `Directory.Packages.props`, `Pepa.slnx`, `.editorconfig`.
- Папка данных во время работы — `%LOCALAPPDATA%\Pepa` или путь из `--data-dir`: `pepa.db`, `backups/`, `logs/`,
  `snapshots/`, `plugins/<id>/`, `plugins-data/<id>/`, `telegram/`, `email/`, `watches/`, `browser-profiles/`,
  `browser-sessions.json`, `adspower-started.json`, `instance.lock`, `desktop.json`, `WebView2/`.

## 4. Команды

Нужны .NET 10 SDK и Node.js 22.

**Сборка и тесты на Linux — как в CI (задание `linux`):**
```bash
cd ui && npm ci && npm test && npm run build && cd ..   # vitest; tsc -b + vite → src/Pepa.Host/wwwroot
PEPA_TEST_BROWSER_PATH=/usr/bin/google-chrome dotnet test Pepa.slnx
```
- Тесты браузера ищут браузер так: `PEPA_TEST_BROWSER_PATH`, потом `PEPA_TEST_BROWSER_CHANNEL` (`msedge`/`chrome`), потом
  `/opt/pw-browsers/chromium-*/chrome-linux64/chrome`, потом Edge на Windows. Если браузер не найден, тесты браузера и
  AdsPower **пропускаются** (около 55 тестов), а код возврата остаётся 0 — ищите в выводе «Skipped». С
  `PEPA_TEST_REQUIRE_BROWSER=1` (так в CI) они вместо этого падают: зелёный прогон без браузера невозможен.
- Длительность: около 950 тестов .NET, примерно 4 минуты, из них около 2,5 минуты — тесты с настоящим браузером;
  несколько тестов только для Windows на Linux пропускаются; около 60 тестов vitest идут секунды. CI на Windows запускает
  `dotnet test Pepa.slnx -c Release`, на Linux — без `-c`, то есть Debug.
- Часть тестов: `dotnet test tests/Pepa.Engine.Tests --filter "FullyQualifiedName~RecoveryTests"`. Ищите тест по имени
  класса, а не файла (`DesktopApiTests` лежит в `TelegramApiTests.cs`). Только типы интерфейса —
  `cd ui && npm run typecheck`: `npm test` типы не проверяет, их проверяет `npm run build`.

**Сквозная проверка интерфейса (e2e):**
```bash
cd ui && npm run build && cd ..
dotnet build src/Pepa.Server                   # e2e берёт src/Pepa.Server/bin/Debug/net10.0/pepa-server.dll
cd ui && npm install --no-save playwright@1.63.0 && npx playwright install chromium && npm run e2e
```
- В CI браузер ставится командой `npx playwright install --with-deps chromium`. Версия npm-пакета `playwright` должна
  совпадать с `Microsoft.Playwright` в `Directory.Packages.props`; в `package.json` его нет.
- Переменные: `PEPA_SERVER_DLL`, `PEPA_E2E_PORT` (по умолчанию 5199; на следующем — имитация Claude API),
  `PEPA_E2E_BROWSER`. Скриншоты — в `ui/e2e/screenshots/` (не в git). Любая ошибка в консоли браузера роняет прогон.

**Запуск без окна (разработка):**
```bash
cd ui && npm ci && npm run build && cd ..
dotnet run --project src/Pepa.Server -- --data-dir ./.data --port 5180 --token dev
# открыть http://127.0.0.1:5180/#token=dev
```
- Интерфейс с горячей перезагрузкой: сервер как выше, затем `cd ui && npm run dev` и `http://localhost:5173/#token=dev`.
  Vite проксирует `/api` на `127.0.0.1:5180`.
- Флаги `pepa-server`: `--data-dir`, `--workspace` (действует только при первом запуске), `--port`, `--token`, `--ui`,
  `--no-samples`, `--browser`, `--assistant-url`. Коды выхода: 1 — неизвестный флаг (печатается справка), 2 — Pepa уже
  запущена с этой папкой данных. Папка `.data/` в git не попадает.
- Без окна блоки окон Windows и запись в окнах недоступны (`UnavailableDesktop`) и отказывают с причиной.

**Сборка для Windows:**
```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1              # интерфейс, тесты, out\Pepa\Pepa.exe
powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipTests
dotnet publish src/Pepa.Desktop/Pepa.Desktop.csproj -c Release -r win-x64 --self-contained true -o out/Pepa -p:DebugType=None -p:GenerateDocumentationFile=false
```
`build.ps1` хранится в UTF-8 с BOM: без BOM Windows PowerShell 5.1 не разберёт русские строки, и CI проверяет, что файл
разбирается. Сценарий останавливается на первой упавшей команде (`Invoke-Checked`). CI на Windows собирает приложение и
выкладывает артефакт `Pepa-win-x64`.

**Проекты только для Windows: `src/Pepa.Desktop` и `src/Pepa.Adapters.Desktop.Windows`.**
- Оба нацелены на `net10.0-windows` с `EnableWindowsTargeting`. На Linux они компилируются (входят в
  `dotnet build/test Pepa.slnx`), но не запускаются, и автоматических тестов у них нет.
- Как с этим жить: держите логику в кроссплатформенных проектах за интерфейсами (`IDesktop`, `IDesktopInputMonitor`,
  `IDesktopAlerts`, `IWakePlatform`) и тестируйте её на имитациях (`FakeDesktop`, `FakeInputMonitor`,
  `FakeWakePlatform`). Windows-слой делайте тонким и на Linux убедитесь хотя бы, что он компилируется. Добавьте ручную
  проверку в `docs/CHECKLIST.md` (§1 «Окно Pepa», §5 «Окна Windows», §6 «Запись в окнах Windows»).
- Вызовы Windows API в общем коде (`PowerKeeper`, `WindowsWakePlatform`) обёрнуты в `OperatingSystem.IsWindows()`.

## 5. Соглашения

**Сборка и зависимости**
- `Directory.Build.props` задаёт `net10.0`, `Nullable`, `ImplicitUsings`, `LangVersion latest` и
  `TreatWarningsAsErrors=true`: любое предупреждение ломает сборку. Не глушите его без причины; если пришлось — с
  комментарием, почему.
- Версии пакетов NuGet задаются только в `Directory.Packages.props` (`<PackageVersion>`). В `.csproj` пишется
  `<PackageReference Include="X" />` без `Version`. В `ui/package.json` версии точные, есть `package-lock.json`; ставьте
  зависимости через `npm ci`.
- Новый проект добавляется в `Pepa.slnx`, в его `.csproj` — однострочный русский комментарий, как у соседей, и
  `<InternalsVisibleTo Include="….Tests" />`. Тестовый проект получает xUnit v3 и `OutputType Exe` из
  `tests/Directory.Build.props`. P/Invoke — через `DllImport` с простыми сигнатурами: `LibraryImport` требует
  `AllowUnsafeBlocks`.

**Стиль кода** (`.editorconfig`)
- Файлы: UTF-8, LF, отступ 4 пробела; 2 пробела для json/yml/xml/csproj/props/ts/tsx/css/html/js. В C# пространства
  имён file-scoped. Файлы `*.ps1` — в UTF-8 с BOM.
- Следуйте стилю соседнего кода: `sealed`-классы, `record`, первичные конструкторы. Комментарии и XML-документация — на
  русском и объясняют «почему», а не «что».

**Тесты**
- Каждое изменение поведения сопровождается тестом, который падает на старом коде; для ошибки сначала пишется
  воспроизводящий её тест. `docs/TESTING.md` («Проверка самих тестов») описывает, как проверяли, что тесты ловят поломки.
- Для надёжности проверяйте четыре вещи: сбой между действием и записью итога, перезапуск, неизвестный итог (Verify:
  `Done`/`NotDone`/`Inconclusive`), отсутствие второго внешнего эффекта при повторе.
- Имя теста — английское предложение через подчёркивания, описывающее поведение:
  `Crash_between_action_and_saving_result_never_repeats_the_action`. Класс называется `<Тема>Tests`.
- Тесты движка: `await using var h = await EngineHarness.CreateAsync(options?, withFaults?, extraNodes?)` поднимает
  настоящий SQLite в `%TEMP%/pepa-tests/<guid>/` и `FakeTimeProvider` со стартом 2026-03-02 09:00 UTC. Сценарий — через
  `ScenarioBuilder` (`.Node(id, type, new { … })`, `.Edge`, `.Chain`, `.Variable`, `.Build`); внешнее действие —
  `TestActionNode` со счётчиками `h.World.Executions` и `EffectCount`; сбои — `FaultInjectingStore`
  (`h.Faults.FailWhenStep`, `h.CrashAndRestartAsync()`); результат ждут через `h.WaitForStatusAsync`, `WaitForAsync`,
  `UntilAsync`, `IdleAsync`.
- Тесты адаптеров подключают `tests/Pepa.Engine.Tests/Infrastructure/**` через `<Compile Include … LinkBase>`. Тесты
  хоста: `await using var f = await HostFixture.StartAsync(seedSamples: false)`, затем `f.SendAsync` и `f.GetAsync`.
- Внешние сервисы — только имитации, настоящие сервисы тесты не трогают: `FakeTelegram`, `FakeImapServer`,
  `FakeSmtpServer`, `FakeAdsPower`, `FakeAnthropic`, `FakeHttp`/`LocalServer`, `TestSite`, `FakeDesktop`.
- Тесты интерфейса: vitest находит только `ui/src/**/*.test.ts` и работает в среде `node`, без DOM, поэтому логику
  выносите в `ui/src/lib/*.ts`. Компоненты проверяет только e2e (`ui/e2e/smoke.cjs`).
- Время: в движке и блоках — только `context.Time` или внедрённый `TimeProvider`, тайм-ауты —
  `new CancellationTokenSource(timeout, timeProvider)`. Нельзя: `DateTime.Now`, `DateTimeOffset.UtcNow`, `Task.Delay`
  без `TimeProvider`.

**Тексты**
- Интерфейс полностью на русском. Подписи enum, кнопки решений с подсказками (`ru.actions`), подтверждения, навигация и
  общие слова лежат в `ui/src/i18n/ru.ts`; карты enum там объявлены с `satisfies Record<…>`, поэтому новое значение enum
  без подписи не скомпилируется. Разовые тексты пишутся прямо в JSX страниц и компонентов — так делается сейчас, хотя
  шапка `ru.ts` и D-015 говорят, что все строки в `ru.ts`. Массово переносить их не нужно.
- Даты, числа и склонения форматируются через `ui/src/lib/format.ts` (`plural`, `formatDateTime`, `relative`…), без
  ручных окончаний. Названия кнопок в текстах — точной подписью и в «ёлочках»: «Исправить и повторить». Тире — «—»,
  многоточие — «…».
- Сообщения для пользователя (`EngineOperationException`, `NodeInputException`, `Failure.Error`, `ErrorDto`,
  `SettingsValidationException`) пишутся на русском и понятны не программисту: что случилось и что безопасно сделать
  дальше. В них нет стека, голого английского текста библиотеки и секретов. Интерфейс показывает сообщения сервера как
  есть и не переписывает их.

**Хранилище и данные**
- Миграции только добавляются в конец массива `Migrations` в `src/Pepa.Storage.Sqlite/SqliteStore.Schema.cs`, с
  комментарием `// N: зачем`. Существующие миграции не меняются никогда.
- `PRAGMA user_version` равен числу миграций (сейчас 9). Старое приложение отказывается открывать более новую базу.
  Резервная копия `backups/pepa-migrationN-*.db` делается автоматически.
- Enum в базе хранятся строками: пишутся через `ToString()`, читаются через `Enum.Parse`, в SQL встречаются литералами
  вида `'Waiting'`. Переименование члена enum требует миграции.
- Время пишется только через `SqliteMapping.Date`/`DateOrNull`: это строка UTC фиксированного формата, и SQL сравнивает
  её как строку. Параметры SQL — `$имя`.
- JSON сценариев, API и экспорта — только через `PepaJson.Options`: camelCase, enum строками в camelCase, кириллица без
  экранирования, `null` записываются.
  - Уже существующие исключения: `processes.debug`, `processes.secrets`, `events.secrets` и `desktop.json` сериализуются
    стандартными настройками System.Text.Json. Без миграции их не переводите.
  - Значения пользователя в JS-условия (`MatchExpression`) вставляйте только через `JsonSerializer.Serialize`.
- API работает только под `/api` с заголовком `X-Pepa-Token`, Kestrel слушает только loopback. CORS нет и не будет.

**Документы** — обновляйте в том же изменении:
- `docs/DECISIONS.md` — новое `D-NNN` в конце (следующий номер после последнего в файле) в формате «решение → почему →
  последствия», или правка существующего. Отменённые решения не удаляются, а помечаются.
- `docs/TESTING.md` — карта тестов: таблица «Уровни», «Обязательные категории» и тематические абзацы.
- `docs/CHECKLIST.md` — если поведение можно проверить только на настоящем ПК или сервисе.
- `docs/ARCHITECTURE.md` — если меняются проекты, API, хранилище или ход исполнения.
- `README.md` — если изменение видно пользователю («Состояние», «Первые шаги», «Структура»); `docs/ROADMAP.md` — если
  меняется этап.

## 6. Рецепты «как добавить»

### 6.1. Новый блок
1. Место: блок логики или потока без ввода-вывода — `src/Pepa.Engine/Nodes/`; блок интеграции —
   `src/Pepa.Adapters.<X>/…Nodes.cs`; блок вне репозитория — расширение (6.9).
2. Класс `public sealed class XNode(…) : INodeExecutor` с `public const string TypeName = "<префикс>.<имя>"` и свойством
   `Descriptor` (`NodeTypeDescriptor`): `Type`, `Category` (`NodeCategories.Logic.Id`, `Flow.Id`, `Files.Id` или
   категория адаптера), `Title` и `Description` на русском, `Icon` (имя lucide в kebab-case), `Effect`,
   `SupportsVerification`, `Outputs` (`PortDescriptor` с `Ports.*`; `PortKind.Error` для `error`, `PortKind.Optional`,
   `LabelField`, `NothingDone`), `Fields` (`FieldDescriptor`: `Name`, `Label`, `Kind`, `Required`, `Default`, `Options`,
   `VisibleWhen`, `Lookup`, `SharedState`), `SummaryField`, `ResourceHint`. Повторно используйте общие поля адаптера:
   `BrowserNodes.SessionField`, `TelegramNodes.ChatField`, `DesktopNodes.WindowField`, `TableNodes.PathField` и т. п.
3. Поведение. Блок не хранит состояние: входные значения читает из `context.Input` (только видимые поля описания, уже
   обработанные `InputResolver`), сырые — из `context.Node.Config`, состояние между шагами держит в `Memory` или
   `Checkpoint`. Для `External` реализуйте `PrepareAsync` (снимок «до» и ключи защиты от повторов) и `VerifyAsync`, итоги
   сообщайте по правилам 2.3; нужен ресурс — `RequiredResource`, долгое действие — `ExpectedDuration`. Действия на
   странице — внутри `BrowserNodes.Run(…)`, в окнах — внутри `DesktopNodes.RunAsync(…)` с `DesktopNodes.InputResource`.
   Дополнительные проверки настройки — `Validate(NodeDefinition)` → `NodeConfigIssue` (значения с `{{` не проверяйте).
4. Регистрация: встроенный блок — в `RegisterLogicNodes()` или `RegisterFlowNodes()`; блок адаптера — в его
   `Register<X>Nodes(this NodeRegistry …)`. Новое семейство блоков допишите в цепочку
   `new NodeRegistry().RegisterLogicNodes()…` в `PepaHost.StartCoreAsync` (`src/Pepa.Host/PepaHost.cs`) до
   `PluginLoader.Load`. Новый раздел палитры — `RegisterCategory(new NodeCategory("id", "Название", порядок))`. Новый
   префикс типа — в `PluginLoader.ReservedPrefixes` (`src/Pepa.Host/Plugins/PluginLoader.cs`).
5. Интерфейс. Палитра, форма свойств и проверка строятся по `GET /api/node-types` сами, но руками добавляются: значок —
   в `nodeIcons` в `ui/src/components/Icon.tsx` (иначе блок покажется как `Box`); новая категория — `case` в
   `categoryColor` и переменная `--cat-<id>` во **всех трёх** блоках темы в `ui/src/styles/app.css`; особая сводка на
   карточке — `nodeSummary`, цвета выходов — `edgeClasses` (`ui/src/lib/graph.ts`); новый `Lookup` — выбор значения в
   `ui/src/pages/editor/FieldEditor.tsx`; ручные списки блоков — `integrations` в `ui/src/pages/IntegrationsPage.tsx`.
6. ИИ-помощник: каталог блоков строится сам из реестра. Если блок неочевиден в использовании, добавьте правило в
   `ScenarioPrompt.BuildSystem` (`src/Pepa.Assistant/ScenarioPrompt.cs`), рядом с правилами `telegram.wait`, `table.*`,
   `email.*`.
7. Рекордеры. Если блок получается из записи действий, обновите места, где типы и поля записаны строками: браузер —
   `ui/src/lib/recording.ts` и `recording.test.ts`; окна — `src/Pepa.Adapters.Desktop/DesktopRecording.cs` и
   `DesktopRecordingTests`.
8. Тесты: модульный на имитации `PrepareAsync` → `ExecuteAsync` → `VerifyAsync` (образцы — `TableTestKit`,
   `HttpNodeTests`); с движком — `EngineHarness.CreateAsync(extraNodes: r => r.Register(new XNode(fake)))`; сбой —
   `withFaults: true`, `h.Faults.FailWhenStep = s => s is { NodeId: "a", State: StepState.Succeeded }`,
   `h.CrashAndRestartAsync()`, затем `ResumeAction.Verify`, и при повторе нет второго эффекта. Правила `Validate` — в
   `tests/Pepa.Core.Tests/ValidatorTests.cs`; описание в API — `ApiTests.Node_types_describe_fields_and_ports`.
9. Документы: D-NNN, `TESTING.md`, `CHECKLIST.md` (если блок работает с настоящим сервисом), README.

### 6.2. Новый вид поля (`FieldKind`)
1. Сервер: член `FieldKind` в `src/Pepa.Core/Nodes/NodeTypeDescriptor.cs` (в JSON станет camelCase); проверка значения в
   `switch` метода `ScenarioValidator.ValidateConfig`; обработка при исполнении в `src/Pepa.Engine/InputResolver.cs` —
   без неё значение передаётся блоку как есть.
2. Помощник: `case` в `KindName` (`ScenarioPrompt.cs`) и приведение значения в `src/Pepa.Assistant/DraftConverter.cs`.
   Без них молча сработает запасной вариант.
3. Интерфейс: строка в объединение `FieldKind` в `ui/src/api/types.ts`; `case` в `switch (field.kind)` в
   `FieldEditor.tsx` (ветка `default:` рисует однострочное поле, и TypeScript о пропуске не предупредит); вставка
   переменных — `isExpression` или `isTemplate` в `WithInserts`; если значение — имя переменной, добавьте вид в
   `referencedVariables` (`ui/src/lib/clipboard.ts`); сводка на карточке — `nodeSummary`.
4. Тесты: `ValidatorTests`, тест движка на обработку значения, `ui/src/lib/graph.test.ts`.

### 6.3. Новая интеграция (адаптер)
1. Проект `src/Pepa.Adapters.Foo`: ссылка на `Pepa.Core` (и на `Pepa.Adapters.FileSystem`, если адаптер работает с
   файлами), пакеты через `Directory.Packages.props`, `InternalsVisibleTo`, запись в `Pepa.slnx`, `ProjectReference` в
   `src/Pepa.Host/Pepa.Host.csproj`.
2. Шлюз — интерфейс вроде `ITelegramGateway` или `IDesktop` и свои исключения двух видов: «не выполнено» (недоступно,
   не найдено, отказ с признаком «можно повторить») и «итог неясен». Состояние — в `<data>/foo/`, файлы пишутся атомарно
   (временный файл + замена), учётные данные — через `Secrets.Protect(value, "Pepa.Foo")`. У сервиса есть
   `ILogger Logger { get; set; } = NullLogger.Instance`, `event Action? StatusChanged`, `Status` и `IAsyncDisposable`.
3. Блоки: `FooNodes.RegisterFooNodes(this NodeRegistry r, …)` с `NodeCategory("foo", "…", порядок)`; все типы с
   префиксом `foo.`, префикс — в `PluginLoader.ReservedPrefixes`. Входящие события — по рецепту 6.7.
4. `PepaHost.StartCoreAsync`: создайте сервис до реестра (если он публикует события, передайте
   `input => engineRef!.PublishEventAsync(input)`), добавьте `.RegisterFooNodes(…)` в цепочку и
   `builder.Services.AddSingleton(foo)`. После `Build` задайте `foo.Logger` и подпишите
   `foo.StatusChanged += () => events.Publish("integrations")`. Запускайте сервис только после `engine.StartAsync`: раньше
   `engineRef` равен `null`, и публикация упадёт. Освобождайте сервис в `StopCoreAsync`: до `engine.StopAsync`, если он
   публикует события, и после — если он нужен идущим шагам; ошибки при освобождении не должны вылетать наружу.
5. Тестовые настройки: свойство в `PepaHostOptions` (адрес сервера, тайм-ауты), которое `HostFixture.Options`
   (`tests/Pepa.Host.Tests/HostFixture.cs`) направляет на имитацию.
6. API: `GET`, `PUT`, `POST …/check`, `DELETE` под `/api/integrations/foo` в `ApiEndpoints.cs`, записи в `Dtos.cs`.
   Секреты передаются только в запросах, ответы содержат признак или подсказку. Ошибки адаптера отдавайте через
   `ErrorDto` с кодом 409 или 502.
7. Интерфейс: типы и вызовы (`types.ts`, `client.ts`), карточка в `ui/src/components/` (образец — `EmailCard.tsx`) на
   странице `ui/src/pages/IntegrationsPage.tsx`. Обновление по `integrations` в `events.ts` уже есть. Добавьте также
   `categoryColor`, `--cat-foo` и значки.
8. Тесты: `tests/Pepa.Adapters.Foo.Tests` (ссылки на адаптер, `Pepa.Engine`, `Pepa.Storage.Sqlite`, пакет
   `Microsoft.Extensions.TimeProvider.Testing`, связанные файлы из `Pepa.Engine.Tests/Infrastructure`) с записью в
   `Pepa.slnx`. Нужен `FakeFoo` (образец — `FakeTelegram`) или имитация сервера на `127.0.0.1:0` (`FakeImapServer`,
   `FakeAdsPower`). Проверьте одно событие дважды, догрузку после `RestartAsync`, тайм-ауты через `h.Time` и API
   (`tests/Pepa.Host.Tests/FooApiTests.cs`).
9. Документы: `ARCHITECTURE.md` (проекты, источники, файлы данных), D-NNN, `TESTING.md`, `CHECKLIST.md`, `ROADMAP.md`,
   README.

### 6.4. Новый адрес API, клиент и хук в интерфейсе
1. Записи запроса и ответа — в `src/Pepa.Host/Api/Dtos.cs`: публичные позиционные `record`, необязательные поля в конце
   со значениями по умолчанию.
2. Маршрут — в `ApiEndpoints.MapPepaApi` на группе `api`, рядом с комментарием своего раздела. Сервисы берутся по типу
   (синглтоны из `PepaHost`), плюс `CancellationToken ct`; токен и `Host` проверяет `LocalAccessMiddleware`. Поля запроса,
   которые могут не прийти, проверяйте сами: отсутствующее свойство JSON приходит как `null`. Ошибки — исключениями из
   2.9 или `Results.Json(new ErrorDto("…по-русски…"), PepaJson.Options, statusCode: …)`.
3. Всё, что пришло из процесса, события, вопроса или триггера, отдавайте через `ProcessMapper`, `SecretMasker` или
   `ProcessEngine.Redact`. Изменилось то, что показывает интерфейс, — `events.Publish("<тип>", id)` (`EventHub`);
   сообщение только `{type, id}`, без данных.
4. Интерфейс: тип — в `ui/src/api/types.ts` (поле, которого может не быть в старых ответах, помечайте `?`); метод — в
   объекте `api` в `ui/src/api/client.ts`: `name: (…) => request<T>("POST", "/path", body)`, путь относительно `/api`,
   параметры через `encodeURIComponent`. Чтение — хук в `ui/src/api/hooks.ts` с постоянным `queryKey`; запись — вызов
   `api`, затем `queryClient.invalidateQueries`, затем `feedback.toast` или `feedback.error`. Новый тип SSE — ветка в
   `handle()` в `ui/src/api/events.ts`, иначе часть интерфейса устареет до следующего опроса.
5. Тесты: `tests/Pepa.Host.Tests` (`f.SendAsync(HttpMethod.Post, "/api/…", new { … })`) — код ответа, текст `error`,
   отсутствие секретов. Логика интерфейса — в vitest; заметный путь пользователя — шаг в `ui/e2e/smoke.cjs`.
6. Документы: «Основные адреса» в `ARCHITECTURE.md`; D-NNN, если меняется правило.

### 6.5. Миграция базы данных
1. Допишите строку в конец `Migrations` (`SqliteStore.Schema.cs`) с комментарием `// 9: зачем`. Используйте
   `ALTER TABLE … ADD COLUMN … NULL` или `… DEFAULT …`; таблицу пересобирайте по образцу миграции 5.
2. Добавьте свойство в запись в `src/Pepa.Engine/Persistence/Records.cs` с разумным значением по умолчанию.
3. `SqliteWrite.cs`: колонка в `INSERT` и в `Bind<Запись>`. Если поле изменяемое — и в `UPDATE`: `UpdateProcessAsync`
   обновляет только часть колонок, остальные записываются один раз при вставке.
4. Чтение — в `SqliteMapping.cs` (`Read<Запись>`): даты через `Date()`, enum через `EnumOf`.
5. Новый метод хранилища объявляется в `IPepaStore` или `IStoreWrite` и реализуется во **всех** реализациях:
   `SqliteStore`, `SqliteWrite`, `FaultInjectingStore` (вместе с вложенной `FaultInjectingWrite`) и `HookStore` в
   `tests/Pepa.Engine.Tests/ReviewRegressionTests.cs`.
6. Колонка, где могут быть секреты, при показе маскируется (`SecretMasker`, `ProcessMapper`). Подумайте и о
   `HistoryRetention` и `PruneEventsAsync`.
7. Тесты: круговой тест в `StoreTests` и обновление старой базы по образцу
   `BackupTests.Database_is_copied_before_its_schema_is_updated` (внутренний `InitializeAsync(N-1)`, вставить данные,
   затем `InitializeAsync()` и прочитать).

### 6.6. Новая настройка
1. Обычная (не секрет, без перезапуска): свойство со значением по умолчанию в `PlatformSettings`
   (`src/Pepa.Host/Settings/SettingsService.cs`), проверка в `SettingsService` (`Check(value, min, max, "Название")`).
   Потребители читают значение вживую через замыкание `() => settings.Current.X`.
2. Настройка движка: опция в `EngineOptions` (`src/Pepa.Engine/EngineContracts.cs`) с безопасным значением по умолчанию и
   перенос в `PlatformSettings.ToEngineOptions()`. Применяется только после перезапуска (`settings.Applied`,
   `RestartRequired`).
3. API: `X? = null` в конец `SettingsDto` (`Dtos.cs`, «null — не менять»); в `PUT /api/settings` (`ApiEndpoints.cs`)
   строка `X = request.X ?? current.X` — обработчик собирает `PlatformSettings` заново, и забытое поле тихо сбросится при
   каждом сохранении; значение в `ToDto(SettingsService)` и нужные `Refresh()`.
4. Интерфейс: поле в `Settings` в `ui/src/api/types.ts`, элемент в `ui/src/pages/SettingsPage.tsx`. Страница отправляет
   весь объект целиком; числа — через помощник `number()`.
5. Тесты: `HostLifecycleTests.Settings_are_validated_and_persisted` (ошибка диапазона даёт 400, значение переживает
   `f.RestartAsync()`) или `IntegrationSettingsTests`.
6. Секрет (ключ, пароль) в `PlatformSettings` не кладите — сделайте как `AssistantKeyStore`: отдельный ключ в таблице
   настроек, `Secrets.Protect(value, "Pepa.<Назначение>")`, `CryptographicException` при чтении означает «не задано»;
   `PUT` и `DELETE` для изменения, `GET` возвращает только `Configured` и подсказку. Тест — что значение не появляется ни
   в одном ответе (образцы — `SecretApiTests`, `AssistantApiTests.Key_is_stored_without_being_returned…`).

### 6.7. Новый источник событий
1. Публикация: `engine.PublishEventAsync(new EventInput { Source = "foo", Type, ExternalId, CorrelationKey, Payload,
   OccurredAt })`. `ExternalId` — стабильный id из самого источника, по нему отсекаются повторы. `OccurredAt` — время по
   часам источника; найденному при догрузке ставьте время раньше `engine.StartedAt`: ждущие процессы событие получат, а
   автозапуск не сработает.
2. Если источник может пропускать события (Pepa закрыта, нет сети, компьютер спал), вызывайте
   `engine.SourceCatchingUp(source, "название")` перед `engine.StartAsync`, при обрыве связи и в обработчике
   `engine.Woke`, а после догрузки — `SourceCaughtUp(source)`. Иначе тайм-аут опередит ответ, который пришёл вовремя
   (D-033, D-080).
3. Блок ожидания возвращает `NodeResult.WaitForEvent(new EventSubscriptionSpec { Source = …, … }, timeoutAt)`: срок от
   `context.Time`, выходы `Ports.Event`, `Ports.Timeout` (`PortKind.Optional`), `Ports.Error`. Значения пользователя
   попадают в `MatchExpression` только через `JsonSerializer.Serialize`; заданный, но пустой после подстановки фильтр —
   `NodeInputException`.
4. Не используйте `pepa.process` и `pepa.question`. Источники сравниваются без учёта регистра.
5. Тесты по образцу `EventTests`, `SleepTests`, `WaitWindowTests`: одно событие дважды, перезапуск, тайм-аут при
   догрузке.

### 6.8. Новый пример сценария
1. Метод `public static ScenarioDefinition MySample()` в `src/Pepa.Host/Samples/SampleScenarios.cs` с именем
   «Пример 17. …»; узлы и связи — через помощники `N(…)` и `E(…)`.
2. Добавьте `(10, MySample())` в `Versioned()` и увеличьте `CurrentVersion` до 10. У существующих пользователей
   появится только новый пример; удалённые примеры не возвращаются.
3. Пример должен проходить проверку без ошибок и предупреждений (`ApiTests.Samples_are_seeded_once_and_valid`); если он
   работает без сети, добавьте его в `ApiTests.Sample_runs_to_completion`. Обновите число примеров («шестнадцать») в
   README («Первые шаги») и в `docs/CHECKLIST.md` §1. Не переименовывайте примеры, на имена которых опирается e2e.

### 6.9. Расширение (свои блоки без пересборки Pepa)
1. Скопируйте `samples/Pepa.Plugins.Sample`: библиотека `net10.0` со ссылкой на `Pepa.Core` с `Private="false"` (сборка
   `Pepa.Core.dll` не должна лежать рядом с расширением).
2. Публичный класс с конструктором без параметров реализует `IPepaPlugin`: `Name` и
   `Register(r) => r.AddCategory(new NodeCategory("myco", "…", 100)).AddNode(…)`.
3. `PluginLoader` принимает расширение целиком или не принимает вовсе. Правила: тип с точкой, не встроенный префикс,
   без повторов; не начало, не конец и не узел разделения или слияния; `SupportsVerification` только при
   `Effect = External`; заданы `Title` и `Description`; категория существует или добавлена этим расширением.
4. Файлы — только через `r.ResolveWorkspacePath`; свои данные — в `r.DataDirectory` (`<data>/plugins-data/<id>/`).
5. Установка — в `%LOCALAPPDATA%\Pepa\plugins\<id>\`. Если Pepa запущена от имени администратора, расширения не
   загружаются. Что загрузилось, видно в «Настройки» → «Расширения» (`GET /api/plugins`).
6. Изменения самого загрузчика проверяются в `tests/Pepa.Host.Tests/PluginLoaderTests.cs` и `PluginApiTests.cs`. Сборка
   образца копируется в `bin/…/sample-plugin` целью `CopySamplePlugin`.

## 7. Подводные камни

**Время и тесты**
- В `EngineHarness` время фейковое (`FakeTimeProvider`). Ожидания, повторы с задержкой, тайм-ауты шагов и расписания
  срабатывают, только когда тест двигает время (`WaitForAsync(…, advance:)`, `IdleAsync`, `h.Time.Advance`).
  Заблокированный `TestActionNode` без сдвига времени не завершится по тайм-ауту. Если сдвинуть сразу слишком много,
  можно перепрыгнуть `ScheduleLateGrace` (10 мин), и запуск будет посчитан пропущенным. Движок работает в фоне
  (`StartProcessAsync` и `ResumeAsync` возвращаются до исполнения): проверяйте только после ожидания нужного состояния.
- Не всё идёт по фейковому времени: маршрутизатор событий опирается на сигнал и на настоящие 5 с; ожидания окон,
  `browser.waitAny`, Playwright, MailKit и WTelegram идут по настоящим часам — держите в тестах `waitSeconds` малым.
- Особенности часов в тестах: в `EngineHarness` определение сна выключено (`SleepGap = null`);
  `FakeTimeProvider.LocalTimeZone` — UTC (важно для «до времени» и ежедневных расписаний); `FolderWatcher` с замороженным
  временем не планирует сканирование, поэтому `FolderWatchTests` подменяет часы (`ScanEveryTime`).
- `PepaHost` жёстко использует `TimeProvider.System`, поэтому тесты хоста идут в реальном времени. Зависимое от времени
  проверяйте, создавая класс напрямую (`ManualTime`, `FakeWakePlatform`).
- `HostFixture.ScenarioIdAsync` ищет по началу имени: передавайте «Пример 1.» с точкой, иначе найдётся «Пример 10».
- Изменили интерфейс шлюза или хранилища — почините имитации во всех тестовых проектах. `FakeDesktop`,
  `FakeInputMonitor`, `FakeImapServer`, `FakeSmtpServer` и `FakeAnthropic` подключены ещё и в `Pepa.Host.Tests`. Где
  можно, используйте члены интерфейса с реализацией по умолчанию (как `IDesktop.CaptureAsync`).

**Движок и хранилище**
- Обработчики `EventsChanged` и `TriggersChanged` вызываются после записи без `try/catch`: исключение в них уронит
  вызывающего, хотя запись уже прошла. Все события движка вызываются синхронно на его потоках — обработчики должны быть
  быстрыми и не бросать исключений; исключение в одном подписчике пропускает остальных.
- Чтение через `_store` внутри открытой записи идёт по другому соединению и не видит своих же незаписанных изменений;
  для этого есть методы чтения `IStoreWrite`. Граф процесса кешируется (`_graphs`): меняете определение процесса не
  через `RetryCoreAsync` — обновите кеш.
- `InputResolver` передаёт только видимые поля описания; поля `Text`, `Code`, `Select`, `Number` и т. п. копируются без
  шаблонов. `StepRecord.Input` хранит настоящие секреты.

**Секреты**
- Движок не маскирует значения короче 3 знаков. Значения до 6 знаков и числа маскируются только отдельным словом.
- `AttentionNotifier` отправляет `StatusMessage` как есть: текст новых уведомлений собирайте только из уже маскированных
  источников (`ProcessMapper.Question`, `ProcessEngine.DisplayTitle`).
- Токен доступа виден в трёх местах: в консоли `pepa-server`, в истории браузера после «Открыть в браузере» и в примере
  curl на странице «Интеграции». Не включайте его в скриншоты и отчёты.

**API и настройки**
- Неверный JSON или неверный тип в теле запроса дают **пустой** 400 (без `ErrorDto` и без записи в журнал), а
  непредусмотренное исключение — пустой 500. Неверный метод на существующем пути возвращает JSON 404 «Такого адреса API
  нет.» вместо 405.
- Ответ 400 «Invalid host» означает, что заголовок `Host` не `127.0.0.1` или `localhost` с портом Pepa. Прокси Vite
  должен оставаться с `changeOrigin: true`.
- Слежение за папками меняется только через `PUT /api/watches` (под `WatchesGate`); `PUT /api/settings` только копирует
  текущее значение. `RecordingGate` и `WatchesGate` статические и общие для всех экземпляров хоста в процессе, включая
  параллельные тесты.
- `ScenarioAssistant` строит каталог блоков один раз, в конструкторе: блок, зарегистрированный позже, помощник не увидит.
- `PluginLoader.ReservedPrefixes` сейчас не включает `email.`, `table.`, `http.` и `user.`.
- Запись действий в браузере только проверяет, что профиль свободен, но не занимает его. Запись в окнах занимает
  `desktop:input`.

**Интерфейс и e2e**
- `ui/e2e/smoke.cjs` ищет элементы по **точным русским подписям** («Сохранить», «Проверить и продолжить», «Запустить
  снова», «Движок на связи», «Готово: 2 из 2»…), по `data-testid` и по стабильным классам (`.palette-item`,
  `.react-flow__node[data-id]`, `.badge`, `.field`). Переименовали кнопку или всплывающее сообщение — поправьте
  `smoke.cjs`, советы в `status.ts`, `status.test.ts` и README. Подписи вкладываются друг в друга («Проверить» /
  «Проверить связь»), поэтому e2e ищет с `exact: true`. e2e нужны примеры с точными именами из `SampleScenarios.cs`, так
  что запускайте без `--no-samples`.
- `src/Pepa.Host/wwwroot/` собирается из `ui` (`npm run build` очищает папку) и **в git не хранится**. Руками его не
  правят; статические файлы кладите в `ui/public/`. Сервер раздаёт копию из своего `bin/…/wwwroot`: после `npm run build`
  пересоберите .NET-проект или запустите с `--ui src/Pepa.Host/wwwroot`. Без `index.html` хост показывает страницу
  «Интерфейс не собран».
- В `App.tsx` у `switch` по страницам нет `default`: забытый `case` даёт пустую страницу без ошибки компиляции. `href()`
  в `router.ts` знает параметры только у `editor`, `process` и `history`.
- `FieldEditor` для незнакомого вида поля молча рисует текстовое поле. `Inspector.setConfig` удаляет ключ при
  `undefined` и `""`, после чего действует `Default` описания.
- `ui/src/api/types.ts` пишется руками, без генерации и проверок во время работы: держите его в соответствии с `Dtos.cs`.
- SSE — только сигнал «перечитай» (`{type,id}`, склейка за 150 мс). При переподключении интерфейс сбрасывает все
  запросы. У `node-types` и `scenario` — `staleTime: Infinity`.
- Тёмная тема описана дважды: через `@media (prefers-color-scheme: dark)` и через `[data-theme="dark"]` — меняйте оба
  блока. Склонения вручную ошибаются на 21, 22…: используйте `plural()`.

**Платформа**
- Вне Windows `Secrets.Protect` ничего не шифрует, а `Unprotect` для значения с `dpapi:` бросает
  `CryptographicException` (код считает это «не задано»). Пути там сравниваются с учётом регистра, признак администратора
  не определяется.
- Раннер CI на Windows работает от администратора, поэтому тесты передают `Elevated = false` — иначе расширения не
  загрузились бы. `Directory.Build.targets` ставит библиотекам `PlaywrightPlatform=none`, приложениям и тестам —
  Node-драйвер только для своей платформы.

## 8. Чек-лист перед коммитом

1. Прочитаны D-NNN, которые касаются изменения; ни одно правило из раздела 2 не нарушено.
2. Есть тест, который падает без изменения. Для движка и адаптеров проверены сбой, перезапуск, неизвестный итог и
   отсутствие второго эффекта.
3. Если менялся `ui/` или контракт API — проходит `cd ui && npm ci && npm test && npm run build`.
4. `dotnet test Pepa.slnx` с `PEPA_TEST_BROWSER_PATH` проходит без новых предупреждений и без неожиданных «Skipped».
   Если менялось что-то для Windows, проекты `net10.0-windows` хотя бы компилируются.
5. Если менялись видимые подписи, страницы или примеры — прогнан e2e (`npm run e2e`), `smoke.cjs` обновлён.
6. Нигде не появились секреты: ни в журнале приложения, ни в ответах API, ни в сообщениях, ни в снимках, ни в
   скриншотах. Новые учётные данные хранятся через DPAPI.
7. Сохранённые имена не переименованы (типы блоков, поля, выходы, enum, ключи); миграция только дописана в конец. Новые
   версии пакетов — только в `Directory.Packages.props` или в `ui/package.json` вместе с `package-lock.json`.
8. Документы обновлены — то, что затронуто: `DECISIONS.md`, `TESTING.md`, `CHECKLIST.md` (если нужна ручная проверка),
   `ARCHITECTURE.md`, README, `ROADMAP.md`.
9. Сообщение коммита — по-русски, «Область: что изменилось» («Движок: …», «Сборка: …»).

## 9. Известные ограничения и куда смотреть

- `docs/AUDIT.md` — отчёт аудита: подтверждённые находки и как они исправлены (D-090), опровергнутые, и что осталось
  (раздел «Не исправлено»). Новые находки дописывайте туда же.
- `docs/DECISIONS.md`, D-089, пункт «Не сделано». Несколько процессов ждут ответа в одном чате Telegram, и один из них
  стоит на паузе между вопросом и ожиданием. Тогда ответ может достаться ждущему соседу без вопроса «кому». Обход —
  «Ответить» в Telegram и поле «Ответ на сообщение» у ожидания (D-077).
- Ограничения по замыслу: секреты процессов лежат в базе и копиях открытыми (ключи интеграций — DPAPI);
  журналы `logs/` не ротируются; расширения и папка данных считаются доверенными; решения защиты ввода в окна Windows
  проверяются тестами (`InputGuard`), а сами вызовы Windows и оболочка (`Pepa.Desktop`, `Pepa.Adapters.Desktop.Windows`)
  — только вручную по `docs/CHECKLIST.md`. Если
  `docs/ARCHITECTURE.md` (он переписывается) расходится с кодом, верен код — исправьте документ.
